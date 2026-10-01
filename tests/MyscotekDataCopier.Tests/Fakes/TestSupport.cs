using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xrm.Sdk;
using MyscotekDataCopier.Core;

namespace MyscotekDataCopier.Tests.Fakes
{
    /// <summary>Collects log lines (with their indentation) for assertions.</summary>
    public sealed class ListLogger : ICopyLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel Level, string Message)>();

        public void Log(LogLevel level, string message) => Entries.Add((level, message));

        public IReadOnlyList<string> Lines => Entries.Select(e => e.Message).ToList();

        public IReadOnlyList<string> Messages(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

        /// <summary>Whole log, one "[Level] message" per line, for assertion failure messages.</summary>
        public string Dump() => string.Join(Environment.NewLine, Entries.Select(e => $"[{e.Level}] {e.Message}"));
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; would post asynchronously).</summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) { _handler = handler; }
        public void Report(T value) => _handler(value);
    }

    /// <summary>Builders for test records.</summary>
    public static class TestData
    {
        private static readonly Dictionary<string, string> ActivityIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = "activityid", ["task"] = "activityid", ["phonecall"] = "activityid", ["activitypointer"] = "activityid"
        };

        /// <summary>A record as Retrieve(ColumnSet(true)) returns it: primary id attribute included.</summary>
        public static Entity Record(string entity, Guid id, params (string Name, object Value)[] attributes)
        {
            var record = new Entity(entity) { Id = id };
            record[ActivityIds.TryGetValue(entity, out string primaryId) ? primaryId : entity + "id"] = id;
            foreach ((string name, object value) in attributes) record[name] = value;
            return record;
        }

        public static EntityReference Ref(string entity, Guid id, string name = null) => new EntityReference(entity, id) { Name = name };

        public static OptionSetValue Opt(int value) => new OptionSetValue(value);

        /// <summary>An activityparty as the platform returns it (with the ids the copier must drop).</summary>
        public static Entity Party(EntityReference partyId, int participationTypeMask, string addressUsed = null)
        {
            var party = new Entity("activityparty") { Id = Guid.NewGuid() };
            party["activitypartyid"] = party.Id;
            party["activityid"] = new EntityReference("email", Guid.NewGuid());
            party["participationtypemask"] = new OptionSetValue(participationTypeMask);
            party["ispartydeleted"] = false;
            if (partyId != null) party["partyid"] = partyId;
            if (addressUsed != null) party["addressused"] = addressUsed;
            return party;
        }

        public static EntityCollection Parties(params Entity[] parties) => new EntityCollection(parties.ToList()) { EntityName = "activityparty" };

        /// <summary>The schema most engine tests use (source and destination alike).</summary>
        public static FakeSchemaProvider StandardSchema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name")
                    .String("accountnumber")
                    .Money("revenue").Derived("revenue_base", "revenue", Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Money)
                    .Int("numberofemployees")
                    .Bool("creditonhold")
                    .Picklist("industrycode")
                    .Memo("description")
                    .Decimal("exchangerate")
                    .Lookup("primarycontactid", "contact")
                    .Lookup("parentaccountid", "account")
                    .Lookup("transactioncurrencyid", "transactioncurrency")
                    .Owner().State().SystemAttributes()
                .Entity("contact", "fullname")
                    .String("fullname", create: false, update: false)
                    .String("firstname").String("lastname").String("emailaddress1")
                    .Customer("parentcustomerid")
                    .Lookup("preferredsystemuserid", "systemuser")
                    .Owner().State().SystemAttributes()
                .Entity("email", "subject", "activityid")
                    .Memo("description")
                    .PartyList("to", "account", "contact", "systemuser")
                    .PartyList("from", "systemuser", "queue")
                    .PartyList("cc", "account", "contact", "systemuser")
                    .Lookup("regardingobjectid", "account", "contact")
                    .Owner().State(activeDefaultStatus: 1, inactiveDefaultStatus: 2).SystemAttributes();
            return schema;
        }
    }

    /// <summary>Wires a CopyEngine to fakes: source/destination services, schemas, options, logger, progress.</summary>
    public sealed class Harness
    {
        public FakeOrganizationService Source { get; } = new FakeOrganizationService();
        public FakeOrganizationService Destination { get; } = new FakeOrganizationService();
        public FakeSchemaProvider SourceSchema { get; set; } = TestData.StandardSchema();
        /// <summary>Null means "same as <see cref="SourceSchema"/>".</summary>
        public FakeSchemaProvider DestinationSchema { get; set; }
        public CopyOptions Options { get; } = new CopyOptions();
        public ListLogger Log { get; } = new ListLogger();
        public List<CopyProgress> Progress { get; } = new List<CopyProgress>();
        public CopyEngine Engine { get; private set; }

        public CopySummary Run(string entity, params Guid[] ids) => Run(entity, CancellationToken.None, ids);

        public CopySummary Run(string entity, CancellationToken token, params Guid[] ids)
        {
            Engine = new CopyEngine(Source, Destination, SourceSchema, DestinationSchema ?? SourceSchema, Options, Log);
            return Engine.Copy(entity, ids, new SyncProgress<CopyProgress>(Progress.Add), token);
        }
    }

    /// <summary>Sets SDK metadata properties that only have internal setters.</summary>
    public static class Reflect
    {
        public static T With<T>(this T target, string property, object value)
        {
            PropertyInfo info = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? throw new ArgumentException($"{target.GetType().Name} has no property {property}.");
            info.SetValue(target, value);
            return target;
        }
    }
}
