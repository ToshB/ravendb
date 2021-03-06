﻿// -----------------------------------------------------------------------
//  <copyright file="SqlReplicationTask.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Configuration;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Impl;
using Raven.Database.Indexing;
using Raven.Database.Json;
using Raven.Database.Plugins;
using Raven.Database.Server;
using Raven.Imports.Newtonsoft.Json.Linq;
using Raven.Json.Linq;
using Task = System.Threading.Tasks.Task;
using System.Linq;

namespace Raven.Database.Bundles.SqlReplication
{
	[InheritedExport(typeof(IStartupTask))]
	[ExportMetadata("Bundle", "sqlReplication")]
	public class SqlReplicationTask : IStartupTask
	{
		private const string RavenSqlreplicationStatus = "Raven/SqlReplication/Status";
		private readonly static ILog log = LogManager.GetCurrentClassLogger();

		public event Action AfterReplicationCompleted = delegate { };

		public DocumentDatabase Database { get; set; }

		private List<SqlReplicationConfig> replicationConfigs;
		private readonly ConcurrentDictionary<string, DateTime> lastError = new ConcurrentDictionary<string, DateTime>(StringComparer.InvariantCultureIgnoreCase);

		private PrefetchingBehavior prefetchingBehavior;

		public void Execute(DocumentDatabase database)
		{
			Database = database;
			Database.OnDocumentChange += (sender, notification) =>
			{
				if (notification.Id == null)
					return;
				if (!notification.Id.StartsWith("Raven/SqlReplication/Configuration/", StringComparison.InvariantCultureIgnoreCase))
					return;

				replicationConfigs = null;
				lastError.Clear();
			};

			GetReplicationStatus();

			prefetchingBehavior = new PrefetchingBehavior(Database.WorkContext, new IndexBatchSizeAutoTuner(Database.WorkContext));

			var task = Task.Factory.StartNew(() =>
			{
				using (LogManager.OpenMappedContext("database", database.Name ?? Constants.SystemDatabase))
				using (new DisposableAction(() => LogContext.DatabaseName.Value = null))
				{
					LogContext.DatabaseName.Value = database.Name ?? Constants.SystemDatabase;
					try
					{
						BackgroundSqlReplication();
					}
					catch (Exception e)
					{
						log.ErrorException("Fatal failure when replicating to SQL. All SQL Replication activity STOPPED", e);
					}
				}
			}, TaskCreationOptions.LongRunning);
			database.ExtensionsState.GetOrAdd(typeof(SqlReplicationTask).FullName, k => new DisposableAction(task.Wait));
		}

		private SqlReplicationStatus GetReplicationStatus()
		{
			var jsonDocument = Database.Get(RavenSqlreplicationStatus, null);
			return jsonDocument == null
				                    ? new SqlReplicationStatus()
				                    : jsonDocument.DataAsJson.JsonDeserialization<SqlReplicationStatus>();
		}

		private void BackgroundSqlReplication()
		{
			int workCounter = 0;
			while (Database.WorkContext.DoWork)
			{
				var config = GetConfiguredReplicationDestinations();
				if (config.Count == 0)
				{
					Database.WorkContext.WaitForWork(TimeSpan.FromMinutes(10), ref workCounter, "Sql Replication");
					continue;
				}
				var localReplicationStatus = GetReplicationStatus();
				var leastReplicatedEtag = GetLeastReplicatedEtag(config, localReplicationStatus);

				if(leastReplicatedEtag == null)
				{
                    Database.WorkContext.WaitForWork(TimeSpan.FromMinutes(10), ref workCounter, "Sql Replication");
                    continue;
				}

				var documents = prefetchingBehavior.GetDocumentsBatchFrom(leastReplicatedEtag.Value);
				if (documents.Count == 0 || 
					documents.All(x=>x.Key.StartsWith("Raven/", StringComparison.InvariantCultureIgnoreCase))) // ignore changes for system docs
				{
					Database.WorkContext.WaitForWork(TimeSpan.FromMinutes(10), ref workCounter, "Sql Replication");
					continue;
				}

                var latestEtag = documents.Last().Etag.Value;

                var relevantConfigs =
                    config
                        .Where(x => ByteArrayComparer.Instance.Compare(GetLastEtagFor(localReplicationStatus, x), latestEtag) <= 0) // haven't replicate the etag yet
                        .Where(x => SystemTime.UtcNow >= lastError.GetOrDefault(x.Name)) // have error or the timeout expired
                        .ToList();

                if (relevantConfigs.Count == 0)
                {
                    Database.WorkContext.WaitForWork(TimeSpan.FromMinutes(10), ref workCounter, "Sql Replication");
                    continue;
                }

				try
				{
					var successes = new ConcurrentQueue<SqlReplicationConfig>();
					BackgroundTaskExecuter.Instance.ExecuteAllInterleaved(Database.WorkContext, relevantConfigs, replicationConfig =>
					{
						try
						{
							if (ReplicateToDesintation(replicationConfig, documents))
								successes.Enqueue(replicationConfig);
						}
						catch (Exception e)
						{
							log.WarnException("Error while replication to SQL destination: " + replicationConfig.Name, e);
						}
					});
                    if (successes.Count == 0)
                        continue;
					foreach (var cfg in successes)
					{
						var destEtag = localReplicationStatus.LastReplicatedEtags.FirstOrDefault(x => string.Equals(x.Name, cfg.Name, StringComparison.InvariantCultureIgnoreCase));
						if (destEtag == null)
						{
							localReplicationStatus.LastReplicatedEtags.Add(new LastReplicatedEtag
							{
								Name = cfg.Name,
								LastDocEtag = latestEtag
							});
						}
						else
						{
							destEtag.LastDocEtag = latestEtag;
						}
					}

				    var obj = RavenJObject.FromObject(localReplicationStatus);
					Database.Put(RavenSqlreplicationStatus, null, obj, new RavenJObject(), null);
				}
				finally
				{
					AfterReplicationCompleted();
				}
			}
		}

		private Guid? GetLeastReplicatedEtag(List<SqlReplicationConfig> config, SqlReplicationStatus localReplicationStatus)
		{
			Guid? leastReplicatedEtag = null;
			foreach (var sqlReplicationConfig in config)
			{
				var lastEtag = GetLastEtagFor(localReplicationStatus, sqlReplicationConfig);
				if (leastReplicatedEtag == null)
					leastReplicatedEtag = lastEtag;
				else if (ByteArrayComparer.Instance.Compare(lastEtag, leastReplicatedEtag.Value) < 0)
					leastReplicatedEtag = lastEtag;
			}
			return leastReplicatedEtag;
		}

		private bool ReplicateToDesintation(SqlReplicationConfig cfg, IEnumerable<JsonDocument> docs)
		{
			var providerFactory = TryGetDbProviderFactory(cfg);
			if (providerFactory == null) 
				return false;

			var dictionary = ApplyConversionScript(cfg, docs);
			if (dictionary.Count == 0)
				return true;
			try
			{
				WriteToRelationalDatabase(cfg, providerFactory, dictionary);
				return true;
			}
			catch (Exception e)
			{
				log.WarnException("Failure to replicate changes to relational database for: " + cfg.Name + Environment.NewLine + e.Data["SQL"], e);
				DateTime time, newTime;
				if (lastError.TryGetValue(cfg.Name, out time) == false)
				{
					newTime = SystemTime.UtcNow.AddSeconds(5);
				}
				else
				{
					var totalSeconds = (SystemTime.UtcNow - time).TotalSeconds;
					newTime = SystemTime.UtcNow.AddSeconds(Math.Max(60 * 15, Math.Min(5, totalSeconds + 5)));
				}
				lastError[cfg.Name] = newTime;
				return false;
			}
		}

		private Dictionary<string, List<ItemToReplicate>> ApplyConversionScript(SqlReplicationConfig cfg, IEnumerable<JsonDocument> docs)
		{
			var dictionary = new Dictionary<string, List<ItemToReplicate>>();
			foreach (var jsonDocument in docs)
			{
				if (string.IsNullOrEmpty(cfg.RavenEntityName) == false)
				{
					var entityName = jsonDocument.Metadata.Value<string>(Constants.RavenEntityName);
					if (string.Equals(cfg.RavenEntityName, entityName, StringComparison.InvariantCultureIgnoreCase) == false)
						continue;
				}
				var patcher = new SqlReplicationScriptedJsonPatcher(Database, dictionary, jsonDocument.Key);
				try
				{
					DocumentRetriever.EnsureIdInMetadata(jsonDocument);
					jsonDocument.Metadata[Constants.DocumentIdFieldName] = jsonDocument.Key;
					var document = jsonDocument.ToJson();
					patcher.Apply(document, new ScriptedPatchRequest
					{
						Script = cfg.Script
					});
				}
				catch (Exception e)
				{
					log.WarnException("Could not process SQL Replication script for " + cfg.Name + ", skipping this document", e);
				}
			}
			return dictionary;
		}

		private DbProviderFactory TryGetDbProviderFactory(SqlReplicationConfig cfg)
		{
			DbProviderFactory providerFactory;
			try
			{
				providerFactory = DbProviderFactories.GetFactory(cfg.FactoryName);
			}
			catch (Exception e)
			{
				log.WarnException(
					string.Format("Could not find provider factory {0} to replicate to sql for {1}, ignoring", cfg.FactoryName,
					              cfg.Name), e);

				lastError[cfg.Name] = DateTime.MaxValue; // always error 

				return null;
			}
			return providerFactory;
		}

		private void WriteToRelationalDatabase(SqlReplicationConfig cfg, DbProviderFactory providerFactory,
		                                       Dictionary<string, List<ItemToReplicate>> dictionary)
		{
			using (var commandBuilder = providerFactory.CreateCommandBuilder())
			using (var connection = providerFactory.CreateConnection())
			{
				Debug.Assert(connection != null);
				Debug.Assert(commandBuilder != null);
				connection.ConnectionString = cfg.ConnectionString;
				connection.Open();
				using (var tx = connection.BeginTransaction())
				{
					foreach (var kvp in dictionary)
					{
						// first, delete all the rows that might already exist there
						foreach (var itemToReplicate in kvp.Value)
						{
							using (var cmd = connection.CreateCommand())
							{
								cmd.Transaction = tx;
								var dbParameter = cmd.CreateParameter();
								dbParameter.ParameterName = GetParameterName(providerFactory, commandBuilder, itemToReplicate.PkName);
								cmd.Parameters.Add(dbParameter);
								dbParameter.Value = itemToReplicate.DocumentId;
								cmd.CommandText = string.Format("DELETE FROM {0} WHERE {1} = {2}",
								                                commandBuilder.QuoteIdentifier(kvp.Key),
								                                commandBuilder.QuoteIdentifier(itemToReplicate.PkName),
								                                dbParameter.ParameterName
									);
								try
								{
									cmd.ExecuteNonQuery();
								}
								catch (Exception e)
								{
									e.Data["SQL"] = cmd.CommandText;
									throw;
								}
							}
						}

						foreach (var itemToReplicate in kvp.Value)
						{
							using (var cmd = connection.CreateCommand())
							{
								cmd.Transaction = tx;

								var sb = new StringBuilder("INSERT INTO ")
									.Append(commandBuilder.QuoteIdentifier(kvp.Key))
									.Append(" (")
									.Append(commandBuilder.QuoteIdentifier(itemToReplicate.PkName))
									.Append(", ");
								foreach (var column in itemToReplicate.Columns)
								{
									if (column.Key == itemToReplicate.PkName)
										continue;
									sb.Append(commandBuilder.QuoteIdentifier(column.Key)).Append(", ");
								}
								sb.Length = sb.Length - 2;

								var pkParam = cmd.CreateParameter();
								pkParam.ParameterName = GetParameterName(providerFactory, commandBuilder, itemToReplicate.PkName);
								pkParam.Value = itemToReplicate.DocumentId;
								cmd.Parameters.Add(pkParam);

								sb.Append(") \r\nVALUES (")
								  .Append(GetParameterName(providerFactory, commandBuilder, itemToReplicate.PkName))
								  .Append(", ");

								foreach (var column in itemToReplicate.Columns)
								{
									if (column.Key == itemToReplicate.PkName)
										continue;
									var colParam = cmd.CreateParameter();
									colParam.ParameterName = column.Key;
									SetParamValue(colParam, column.Value);
									cmd.Parameters.Add(colParam);
									sb.Append(GetParameterName(providerFactory, commandBuilder, column.Key)).Append(", ");
								}
								sb.Length = sb.Length - 2;
								sb.Append(")");
								cmd.CommandText = sb.ToString();
								try
								{
									cmd.ExecuteNonQuery();
								}
								catch (Exception e)
								{
									e.Data["SQL"] = cmd.CommandText;
									throw;
								}
							}
						}
					}
					tx.Commit();
				}
			}
		}

		private static void SetParamValue(DbParameter colParam, RavenJToken val)
		{
			if (val == null)
				colParam.Value = DBNull.Value;
			else
			{
				switch (val.Type)
				{
					case JTokenType.None:
					case JTokenType.Object:
					case JTokenType.Uri:
					case JTokenType.Raw:
					case JTokenType.Array:
						colParam.Value = val.Value<string>();
						return;
					case JTokenType.String:
						var value = val.Value<string>();
						if (value.Length > 0)
						{
							if (char.IsDigit(value[0]))
							{
								DateTime dateTime;
								if (DateTime.TryParseExact(value, Default.OnlyDateTimeFormat, CultureInfo.InvariantCulture,
														   DateTimeStyles.RoundtripKind, out dateTime))
								{
									colParam.Value = dateTime;
									return;
								}
								DateTimeOffset dateTimeOffset;
								if (DateTimeOffset.TryParseExact(value, Default.DateTimeFormatsToRead, CultureInfo.InvariantCulture,
														   DateTimeStyles.RoundtripKind, out dateTimeOffset))
								{
									colParam.Value = dateTimeOffset;
									return;
								}
							}
						}
						colParam.Value = value;
						return;
					case JTokenType.Integer:
					case JTokenType.Date:
					case JTokenType.Bytes:
					case JTokenType.Guid:
					case JTokenType.Boolean:
					case JTokenType.TimeSpan:
					case JTokenType.Float:
						colParam.Value = val.Value<object>();
						return;
					case JTokenType.Null:
					case JTokenType.Undefined:
						colParam.Value = DBNull.Value;
						return;
					default:
						throw new InvalidOperationException("Cannot understand how to save " + val.Type + " for " + colParam.ParameterName);
				}
			}
		}


		private string GetParameterName(DbProviderFactory providerFactory, DbCommandBuilder commandBuilder, string paramName)
		{
			switch (providerFactory.GetType().Name)
			{
				case "SqlClientFactory":
				case "MySqlClientFactory":
					return "@" + paramName;

				case "OracleClientFactory":
				case "NpgsqlFactory":
					return ":" + paramName;

				default:
					// If we don't know, try to get it from the CommandBuilder.
					return getParameterNameFromBuilder(commandBuilder, paramName);
			}
		}

		private static readonly Func<DbCommandBuilder, string, string> getParameterNameFromBuilder =
				(Func<DbCommandBuilder, string, string>)
				Delegate.CreateDelegate(typeof(Func<DbCommandBuilder, string, string>),
										typeof(DbCommandBuilder).GetMethod("GetParameterName",
																		   BindingFlags.Instance | BindingFlags.NonPublic, Type.DefaultBinder,
																		   new[] { typeof(string) }, null));


		public class ItemToReplicate
		{
			public string PkName { get; set; }
			public string DocumentId { get; set; }
			public RavenJObject Columns { get; set; }
		}

		private class SqlReplicationScriptedJsonPatcher : ScriptedJsonPatcher
		{
			private readonly Dictionary<string, List<ItemToReplicate>> dictionary;
			private readonly string docId;

			public SqlReplicationScriptedJsonPatcher(DocumentDatabase database, 
				Dictionary<string, List<ItemToReplicate>> dictionary,
				string docId)
				: base(database)
			{
				this.dictionary = dictionary;
				this.docId = docId;
			}

			protected override void RemoveEngineCustomizations(JintEngine jintEngine)
			{
				jintEngine.RemoveParameter("documentId");
				jintEngine.RemoveParameter("sqlReplicate");
			}

			protected override void CustomizeEngine(JintEngine jintEngine)
			{
				jintEngine.SetParameter("documentId", docId);
				jintEngine.SetFunction("sqlReplicate", (Action<string, string, JsObject>)((table, pkName, cols) =>
				{
					var itemToReplicates = dictionary.GetOrAdd(table);
					itemToReplicates.Add(new ItemToReplicate
					{
						PkName = pkName,
						DocumentId = docId,
						Columns = ToRavenJObject(cols)
					});
				}));
			}

			protected override RavenJObject ConvertReturnValue(JsObject jsObject)
			{
				return null;// we don't use / need the return value
			}
		}

		private Guid GetLastEtagFor(SqlReplicationStatus replicationStatus, SqlReplicationConfig sqlReplicationConfig)
		{
			var lastEtag = Guid.Empty;
			var lastEtagHolder = replicationStatus.LastReplicatedEtags.FirstOrDefault(
				x => string.Equals(sqlReplicationConfig.Name, x.Name, StringComparison.InvariantCultureIgnoreCase));
			if (lastEtagHolder != null)
				lastEtag = lastEtagHolder.LastDocEtag;
			return lastEtag;
		}

		private List<SqlReplicationConfig> GetConfiguredReplicationDestinations()
		{
			var sqlReplicationConfigs = replicationConfigs;
			if (sqlReplicationConfigs != null)
				return sqlReplicationConfigs;

			sqlReplicationConfigs = new List<SqlReplicationConfig>();
			Database.TransactionalStorage.Batch(accessor =>
			{
				const string prefix = "Raven/SqlReplication/Configuration/";
				foreach (var document in accessor.Documents.GetDocumentsWithIdStartingWith(
								prefix, 0, 256))
				{
					var cfg = document.DataAsJson.JsonDeserialization<SqlReplicationConfig>();
					if (string.IsNullOrWhiteSpace(cfg.Name))
					{
						log.Warn("Could not find name for sql replication document {0}, ignoring", document.Key);
						continue;
					}
					if (string.IsNullOrWhiteSpace(cfg.ConnectionStringName) == false)
					{
						var connectionString = ConfigurationManager.ConnectionStrings[cfg.ConnectionStringName];
						if (connectionString == null)
						{
							log.Warn("Could not find connection string named '{0}' for sql replication config: {1}, ignoring sql replication setting.",
								cfg.ConnectionStringName,
								document.Key);
							continue;
						}
						cfg.ConnectionString = connectionString.ConnectionString;
					}
					else if (string.IsNullOrWhiteSpace(cfg.ConnectionStringSettingName) == false)
					{
						var setting = Database.Configuration.Settings[cfg.ConnectionStringSettingName];
						if (string.IsNullOrWhiteSpace(setting))
						{
							log.Warn("Could not find setting named '{0}' for sql replication config: {1}, ignoring sql replication setting.",
								cfg.ConnectionStringName,
								document.Key);
							continue;
						}
					}
					sqlReplicationConfigs.Add(cfg);
				}
			});
			replicationConfigs = sqlReplicationConfigs;
			return sqlReplicationConfigs;
		}
	}
}