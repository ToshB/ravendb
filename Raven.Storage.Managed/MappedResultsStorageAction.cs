//-----------------------------------------------------------------------
// <copyright file="MappedResultsStorageAction.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Raven.Abstractions.Extensions;
using Raven.Database.Impl;
using Raven.Database.Storage;
using Raven.Json.Linq;
using Raven.Storage.Managed.Impl;
using System.Linq;
using Raven.Database.Json;

namespace Raven.Storage.Managed
{
    public class MappedResultsStorageAction : IMappedResultsStorageAction
    {
        private readonly TableStorage storage;
        private readonly IUuidGenerator generator;

        public MappedResultsStorageAction(TableStorage storage, IUuidGenerator generator)
        {
            this.storage = storage;
            this.generator = generator;
        }

        public void PutMappedResult(string view, string docId, string reduceKey, RavenJObject data, byte[] viewAndReduceKeyHashed)
        {
            var ms = new MemoryStream();
            data.WriteTo(ms);
			storage.MappedResults.Put(new RavenJObject
            {
                {"view", view},
                {"reduceKey", reduceKey},
                {"docId", docId},
                {"mapResultId", generator.CreateSequentialUuid().ToByteArray()}
            }, ms.ToArray());
        }

    	public IEnumerable<RavenJObject> GetMappedResults(params GetMappedResultsParams[] getMappedResultsParams)
    	{
    		return getMappedResultsParams.SelectMany(GetMappedResults);
    	}

    	public IEnumerable<RavenJObject> GetMappedResults(GetMappedResultsParams getMappedResultsParams)
        {
            return storage.MappedResults["ByViewAndReduceKey"].SkipTo(new RavenJObject
            {
                {"view", getMappedResultsParams.View},
                {"reduceKey", getMappedResultsParams.ReduceKey}
		}).TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), getMappedResultsParams.View) &&
                              StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("reduceKey"), getMappedResultsParams.ReduceKey))
                .Select(x =>
                {
                    var readResult = storage.MappedResults.Read(x);
                    if (readResult == null)
                        return null;
                    return readResult.Data().ToJObject();
                }).Where(x => x != null);

        }

        public IEnumerable<string> DeleteMappedResultsForDocumentId(string documentId, string view)
        {
            foreach (var key in storage.MappedResults["ByViewAndDocumentId"].SkipTo(new RavenJObject
            {
                {"view", view},
                {"docId", documentId}
            }).TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), view) &&
                              StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("docId"), documentId)))
            {
                storage.MappedResults.Remove(key);
                yield return key.Value<string>("reduceKey");
            }
        }

        public void DeleteMappedResultsForView(string view)
        {
            foreach (var key in storage.MappedResults["ByViewAndReduceKey"].SkipTo(new RavenJObject{{"view", view}})
            .TakeWhile(x => StringComparer.InvariantCultureIgnoreCase.Equals(x.Value<string>("view"), view)))
            {
                storage.MappedResults.Remove(key);
            }
        }
    }
}