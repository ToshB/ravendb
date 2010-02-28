﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using log4net;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Rhino.DivanDB.Json;
using Rhino.DivanDB.Linq;

namespace Rhino.DivanDB.Indexing
{
    public class Index : IDisposable
    {
        private ILog log = LogManager.GetLogger(typeof (Index));

        private class CurrentIndexSearcher
        {
            public IndexSearcher Searcher{ get; set;}
            private int useCount;
            private bool shouldDisposeWhenThereAreNoUsages;


            public IDisposable Use()
            {
                Interlocked.Increment(ref useCount);
                return new CleanUp(this);
            }

            private class CleanUp : IDisposable
            {
                private CurrentIndexSearcher parent;

                public CleanUp(CurrentIndexSearcher parent)
                {
                    this.parent = parent;
                }

                public void Dispose()
                {
                    var uses = Interlocked.Decrement(ref parent.useCount);
                    if (parent.shouldDisposeWhenThereAreNoUsages && uses == 0)
                        parent.Searcher.Close();

                }
            }

            public void MarkForDispoal()
            {
                shouldDisposeWhenThereAreNoUsages = true;
            }
        }

        private readonly FSDirectory directory;
        private CurrentIndexSearcher searcher;
        private readonly string name;

        public Index(FSDirectory directory)
        {
            name = directory.GetFile().Name;
            log.DebugFormat("Creating index for {0}", name);
            this.directory = directory;
            searcher = new CurrentIndexSearcher
                       {
                           Searcher = new IndexSearcher(directory)
                       };
        }

        public void Dispose()
        {
            searcher.Searcher.Close();
            directory.Close();
        }

        public IEnumerable<string> Query(string query)
        {
            log.DebugFormat("Issuing query on index {0} for: {1}", name, query);
            var luceneQuery = new QueryParser("", new StandardAnalyzer()).Parse(query);
            using (searcher.Use())
            {
                var search = searcher.Searcher.Search(luceneQuery);
                for (int i = 0; i < search.Length(); i++)
                {
                    yield return search.Doc(i).GetField("_id").StringValue();
                }
            }
        }
        private void Write(Func<IndexWriter, bool> action)
        {
            var indexWriter = new IndexWriter(directory, new StandardAnalyzer());
            bool shouldRcreateSearcher;
            try
            {
                shouldRcreateSearcher = action(indexWriter);
            }
            finally
            {
                indexWriter.Close();
            }
            if (shouldRcreateSearcher)
                RecreateSearcher();
        }

        public void IndexDocuments(IndexingFunc func, IEnumerable<JsonDynamicObject> documents)
        {
            int count = 0;
            Write(indexWriter =>
                  {
                      var docs = func(documents).Cast<object>();
                      var currentId = Guid.NewGuid().ToString();
                      var luceneDoc = new Document();
                      bool shouldRcreateSearcher = false;
                      foreach (var doc in docs)
                      {
                          count++;
                          var fields = new List<Field>();
                          var docId = AddValuesToDocument(fields, doc);
                          if (docId != currentId)
                          {
                              if (luceneDoc.GetFieldsCount() > 0)
                              {
                                  indexWriter.UpdateDocument(new Term("_id", docId), luceneDoc);
                                  shouldRcreateSearcher = true;
                              }
                              luceneDoc = new Document();
                              currentId = docId;
                              luceneDoc.Add(new Field("_id", docId, Field.Store.YES, Field.Index.UN_TOKENIZED));
                          }
                          foreach (var field in fields)
                          {
                              luceneDoc.Add(field);
                          }
                      }
                      if (luceneDoc.GetFieldsCount() > 0)
                      {
                          indexWriter.UpdateDocument(new Term("_id", currentId), luceneDoc);
                          shouldRcreateSearcher = true;
                      }
                      return shouldRcreateSearcher;
                  });
            log.InfoFormat("Indexed {0} documents for {1}", count, name);
        }

        private void RecreateSearcher()
        {
            using (searcher.Use())
            {
                searcher.MarkForDispoal();
                searcher = new CurrentIndexSearcher
                           {
                               Searcher = new IndexSearcher(directory)
                           };
            }
        }

        private static string AddValuesToDocument(ICollection<Field> fields, object val)
        {
            var properties = TypeDescriptor.GetProperties(val).Cast<PropertyDescriptor>().ToArray();
            var id = properties.First(x => x.Name == "_id");

            foreach (PropertyDescriptor property in properties)
            {
                if (property == id)
                    continue;
                var value = property.GetValue(val);
                if (value == null)
                    continue;
                fields.Add(new Field(property.Name, ToIndexableString(value),
                    Field.Store.YES,
                    Field.Index.TOKENIZED));
            }
            return (string)id.GetValue(val);
        }

        private static string ToIndexableString(object val)
        {
            if (val is DateTime)
                return DateTools.DateToString((DateTime)val, DateTools.Resolution.DAY);

            return val.ToString();
        }

        public void Remove(string[] keys)
        {
            Write(writer =>
                  {
                      if(log.IsDebugEnabled)
                      {
                          log.DebugFormat("Deleting ({0}) from {1}", string.Format(", ", keys), name);
                      }
                      writer.DeleteDocuments(keys.Select(k => new Term("_id", k)).ToArray());
                      return true;
                  });
        }
    }
}