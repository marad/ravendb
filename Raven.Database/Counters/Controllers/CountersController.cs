﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Raven.Abstractions.Data;
using Raven.Abstractions.Replication;



namespace Raven.Database.Counters.Controllers
{
	public class CountersController : RavenCountersApiController
	{

		[Route("counters/{counterName}/change")]
		[HttpGet]
		public HttpResponseMessage Change(string group, string counterName, long delta)
		{
			using (var writer = Storage.CreateWriter())
			{
				string counter = String.Join(Constants.GroupSeperatorString, new [] { group, counterName });
				writer.Store(Storage.CounterStorageUrl, counter, delta);

				writer.Commit();
				return new HttpResponseMessage(HttpStatusCode.Accepted);
			}
		}

		[Route("counters/{counterName}/groups")]
		[HttpGet]
		public HttpResponseMessage Groups()
		{
			using (var reader = Storage.CreateReader())
			{
				return Request.CreateResponse(HttpStatusCode.OK, reader.GetCounterGroups().ToList());
			}
		}

		[Route("counters/{counterName}/counters")]
		[HttpGet]
		public HttpResponseMessage Counters(int skip = 0, int take = 20, string counterGroupName = null)
		{
			using (var reader = Storage.CreateReader())
			{
				var prefix = (counterGroupName == null) ? string.Empty : (counterGroupName + Constants.GroupSeperatorString);
				var results = (
					from counterFullName in reader.GetCounterNames(prefix)
					let counter = reader.GetCounter(counterFullName)
					select new CounterView
					{
						Name = counterFullName.Split(Constants.GroupSeperatorChar)[1],
						Group = counterFullName.Split(Constants.GroupSeperatorChar)[0],
						OverallTotal = counter.ServerValues.Sum(x => x.Positive - x.Negative),

						Servers = counter.ServerValues.Select(s => new CounterView.ServerValue
						{
							Negative = s.Negative, Positive = s.Positive, Name = reader.ServerNameFor(s.SourceId)
						}).ToList()
					}).ToList();
				return Request.CreateResponse(HttpStatusCode.OK, results);
			}
		}

		[Route("counters/{counterName}/replications-get")]
		[HttpGet]
		public async Task<HttpResponseMessage> ReplicationsGet()
		{
			using (var reader = Storage.CreateReader())
			{
				return Request.CreateResponse(HttpStatusCode.OK, reader.GetReplicationData());
			}
		}

		[Route("counters/{counterName}/replications-save")]
		[HttpPut]
		public async Task<HttpResponseMessage> ReplicationsSave()
		{
			CounterStorageReplicationDocument newReplicationDocument;
			try
			{
				newReplicationDocument = await ReadJsonObjectAsync<CounterStorageReplicationDocument>();
			}
			catch (Exception e)
			{
				return Request.CreateResponse(HttpStatusCode.BadRequest, e.Message);
			}

			using (var writer = Storage.CreateWriter())
			{
				string updateResult = writer.UpdateReplications(newReplicationDocument);
				writer.Commit();

				HttpStatusCode response = (updateResult != null) ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
				return Request.CreateResponse(response, updateResult);
			}
		}

		public class CounterView
		{
			public string Name { get; set; }
			public string Group { get; set; }
			public long OverallTotal { get; set; }
			public List<ServerValue> Servers { get; set; }


			public class ServerValue
			{
				public string Name { get; set; }
				public long Positive { get; set; }
				public long Negative { get; set; }
			}
		}
	}
}