using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IOrganizationService"/> keyed by (entity, id). Every call - including
    /// the direct Create/Retrieve/Update/RetrieveMultiple methods - is recorded as an
    /// <see cref="OrganizationRequest"/> (with its Parameters) in <see cref="Executed"/>. Like the
    /// platform, an Update that sets a closing state (won/lost opportunity, resolved incident, won/closed
    /// quote, cancelled/fulfilled order) is refused, and the close messages (WinOpportunity,
    /// LoseOpportunity, CloseIncident, WinQuote, CloseQuote, CancelSalesOrder, FulfillSalesOrder) set
    /// the state of the stored record, from the states the platform closes it from. QueryExpressions
    /// are evaluated over the store (AND-ed Equal/Null/NotNull conditions, orders, TopCount and
    /// PageInfo with a paging cookie), e.g. the child-record and systemform queries of SPEC 5.10 - and
    /// the intersect queries of N:N relationships: an association is a row of the intersect entity
    /// (seeded with <see cref="AddAssociation"/>), and an AssociateRequest over a relationship declared
    /// with <see cref="ManyToMany"/> adds one (both records must exist; a pair that is there already
    /// fails with "Cannot insert duplicate key.", like the platform).
    /// </summary>
    public sealed class FakeOrganizationService : IOrganizationService
    {
        public const int ObjectDoesNotExist = -2147220969;   // 0x80040217
        public const int DuplicateRecord = -2147220937;      // 0x80040237
        public const int GenericFailure = -2147220891;       // 0x80040265

        private readonly Dictionary<(string Entity, Guid Id), Entity> _store = new Dictionary<(string Entity, Guid Id), Entity>();

        /// <summary>Every request, in order.</summary>
        public List<OrganizationRequest> Executed { get; } = new List<OrganizationRequest>();

        /// <summary>Creates that throw: "entity" (all records of it) or "entity/guid" (one record).</summary>
        public HashSet<string> FailCreates { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Updates that throw: "entity" or "entity/guid".</summary>
        public HashSet<string> FailUpdates { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A create that includes overriddencreatedon throws (missing override privilege).</summary>
        public bool FailOverriddenCreatedOn { get; set; }

        /// <summary>An update that includes statecode throws, and so does every close message.</summary>
        public bool FailStateChanges { get; set; }

        /// <summary>
        /// RetrieveMultiple of a QueryExpression on one of these entities throws the given exception while
        /// Retrieve by id still works: e.g. a virtual-table provider that fails every query.
        /// </summary>
        public Dictionary<string, Exception> FailRetrieveMultiple { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Retrieves that throw the given exception: key "entity" (all records of it) or "entity/guid" (one record).</summary>
        public Dictionary<string, Exception> FailRetrieves { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        /// <summary>AssociateRequests over one of these relationships (schema names) throw.</summary>
        public HashSet<string> FailAssociates { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The N:N relationships the fake knows (for AssociateRequest and the association helpers), by schema name.</summary>
        public Dictionary<string, ManyToManyRelationship> ManyToManyRelationships { get; } =
            new Dictionary<string, ManyToManyRelationship>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Primary id attribute per entity when it is not "{entity}id".</summary>
        public Dictionary<string, string> PrimaryIdAttributes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = "activityid", ["task"] = "activityid", ["phonecall"] = "activityid",
            ["appointment"] = "activityid", ["letter"] = "activityid", ["fax"] = "activityid",
            ["activitypointer"] = "activityid"
        };

        /// <summary>Called for every request after it is recorded and before it is processed.</summary>
        public Action<OrganizationRequest> BeforeExecute { get; set; }

        /// <summary>Optional override for any request; return null to fall through to the default handling.</summary>
        public Func<OrganizationRequest, OrganizationResponse> ExecuteHandler { get; set; }

        /// <summary>
        /// Optional override for RetrieveMultiple (e.g. FetchExpression or view queries). Returning null
        /// falls through to the evaluation of a QueryExpression over the store.
        /// </summary>
        public Func<QueryBase, EntityCollection> RetrieveMultipleHandler { get; set; }

        // ---- seeding and inspection ----

        public FakeOrganizationService Add(Entity entity)
        {
            if (entity.Id == Guid.Empty) throw new ArgumentException("Seeded records need an id.");
            _store[Key(entity.LogicalName, entity.Id)] = Clone(entity);
            return this;
        }

        /// <summary>Declares an N:N relationship: its intersect entity and, per side, the entity and the intersect attribute.</summary>
        public FakeOrganizationService ManyToMany(string schemaName, string intersectEntity, string entity1, string entity1Attribute,
                                                  string entity2, string entity2Attribute)
        {
            ManyToManyRelationships[schemaName] = new ManyToManyRelationship
            {
                SchemaName = schemaName,
                IntersectEntity = intersectEntity,
                Entity1LogicalName = entity1,
                Entity1IntersectAttribute = entity1Attribute,
                Entity2LogicalName = entity2,
                Entity2IntersectAttribute = entity2Attribute
            };
            return this;
        }

        /// <summary>Seeds an association: a row of the relationship's intersect entity holding the side-1 and side-2 ids.</summary>
        public FakeOrganizationService AddAssociation(string schemaName, Guid entity1Id, Guid entity2Id)
        {
            AddIntersectRow(DeclaredRelationship(schemaName), entity1Id, entity2Id);
            return this;
        }

        /// <summary>The associated pairs (side-1 id, side-2 id) of a relationship: the rows of its intersect entity.</summary>
        public IReadOnlyList<(Guid Entity1Id, Guid Entity2Id)> Associations(string schemaName)
        {
            ManyToManyRelationship relationship = DeclaredRelationship(schemaName);
            return IntersectRows(relationship)
                .Select(e => ((Guid)e[relationship.Entity1IntersectAttribute], (Guid)e[relationship.Entity2IntersectAttribute]))
                .ToList();
        }

        public Entity Get(string entity, Guid id) => _store.TryGetValue(Key(entity, id), out Entity stored) ? stored : null;

        public bool Contains(string entity, Guid id) => _store.ContainsKey(Key(entity, id));

        public IReadOnlyList<CreateRequest> Creates => Executed.OfType<CreateRequest>().ToList();

        public IReadOnlyList<UpdateRequest> Updates => Executed.OfType<UpdateRequest>().ToList();

        public IReadOnlyList<RetrieveRequest> Retrieves => Executed.OfType<RetrieveRequest>().ToList();

        public IReadOnlyList<QueryExpression> Queries =>
            Executed.OfType<RetrieveMultipleRequest>().Select(r => r.Query).OfType<QueryExpression>().ToList();

        /// <summary>The close messages (WinOpportunity, CloseIncident, WinQuote...), in order.</summary>
        public IReadOnlyList<OrganizationRequest> CloseRequests => Executed.Where(IsCloseRequest).ToList();

        /// <summary>The AssociateRequests, in order.</summary>
        public IReadOnlyList<AssociateRequest> Associates => Executed.OfType<AssociateRequest>().ToList();

        /// <summary>Create, Update, Delete and Associate requests, and the close messages.</summary>
        public IReadOnlyList<OrganizationRequest> Writes =>
            Executed.Where(r => r is CreateRequest || r is UpdateRequest || r is DeleteRequest || r is AssociateRequest || IsCloseRequest(r)).ToList();

        public static bool IsCloseRequest(OrganizationRequest request) =>
            request is WinOpportunityRequest || request is LoseOpportunityRequest || request is CloseIncidentRequest
            || request is WinQuoteRequest || request is CloseQuoteRequest
            || request is CancelSalesOrderRequest || request is FulfillSalesOrderRequest;

        public static FaultException<OrganizationServiceFault> Fault(int errorCode, string message) =>
            new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = errorCode, Message = message },
                new FaultReason(message));

        // ---- IOrganizationService ----

        public Guid Create(Entity entity) => (Guid)Execute(new CreateRequest { Target = entity }).Results["id"];

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            (Entity)Execute(new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet }).Results["Entity"];

        public void Update(Entity entity) => Execute(new UpdateRequest { Target = entity });

        public void Delete(string entityName, Guid id) => Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });

        public EntityCollection RetrieveMultiple(QueryBase query) =>
            (EntityCollection)Execute(new RetrieveMultipleRequest { Query = query }).Results["EntityCollection"];

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            Execute(new AssociateRequest { Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities });

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            throw new NotSupportedException("Disassociate is not used by the copier.");

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            Executed.Add(request);
            BeforeExecute?.Invoke(request);

            OrganizationResponse handled = ExecuteHandler?.Invoke(request);
            if (handled != null) return handled;

            switch (request)
            {
                case CreateRequest create: return DoCreate(create.Target);
                case UpdateRequest update: return DoUpdate(update.Target);
                case RetrieveRequest retrieve: return DoRetrieve(retrieve.Target, retrieve.ColumnSet);
                case RetrieveMultipleRequest multiple: return DoRetrieveMultiple(multiple.Query);
                case WinOpportunityRequest win: return DoClose(win, win.OpportunityClose, "opportunityid", 1, win.Status, new WinOpportunityResponse());
                case LoseOpportunityRequest lose: return DoClose(lose, lose.OpportunityClose, "opportunityid", 2, lose.Status, new LoseOpportunityResponse());
                case CloseIncidentRequest resolve: return DoClose(resolve, resolve.IncidentResolution, "incidentid", 1, resolve.Status, new CloseIncidentResponse());
                case WinQuoteRequest winQuote: return DoClose(winQuote, winQuote.QuoteClose, "quoteid", 2, winQuote.Status, new WinQuoteResponse());
                case CloseQuoteRequest closeQuote: return DoClose(closeQuote, closeQuote.QuoteClose, "quoteid", 3, closeQuote.Status, new CloseQuoteResponse());
                case CancelSalesOrderRequest cancel: return DoClose(cancel, cancel.OrderClose, "salesorderid", 2, cancel.Status, new CancelSalesOrderResponse());
                case FulfillSalesOrderRequest fulfil: return DoClose(fulfil, fulfil.OrderClose, "salesorderid", 3, fulfil.Status, new FulfillSalesOrderResponse());
                case AssociateRequest associate: return DoAssociate(associate);
                case DeleteRequest delete:
                    if (!_store.Remove(Key(delete.Target.LogicalName, delete.Target.Id))) throw NotFound(delete.Target.LogicalName, delete.Target.Id);
                    return new DeleteResponse();
                default:
                    throw new NotSupportedException($"FakeOrganizationService does not support {request.GetType().Name} ({request.RequestName}).");
            }
        }

        // ---- request handling ----

        private OrganizationResponse DoCreate(Entity target)
        {
            Guid id = target.Id != Guid.Empty ? target.Id : Guid.NewGuid();
            if (Matches(FailCreates, target.LogicalName, id))
                throw Fault(GenericFailure, $"Simulated create failure for {target.LogicalName} {id}");
            if (FailOverriddenCreatedOn && target.Attributes.ContainsKey("overriddencreatedon"))
                throw Fault(GenericFailure, "Principal user is missing prvOverrideCreatedOnCreatedBy privilege");
            if (_store.ContainsKey(Key(target.LogicalName, id)))
                throw Fault(DuplicateRecord, "Cannot insert duplicate key.");

            Entity stored = Clone(target);
            stored.Id = id;
            _store[Key(target.LogicalName, id)] = stored;

            var response = new CreateResponse();
            response.Results["id"] = id;
            return response;
        }

        private OrganizationResponse DoUpdate(Entity target)
        {
            if (!_store.TryGetValue(Key(target.LogicalName, target.Id), out Entity stored))
                throw NotFound(target.LogicalName, target.Id);
            if (Matches(FailUpdates, target.LogicalName, target.Id))
                throw Fault(GenericFailure, $"Simulated update failure for {target.LogicalName} {target.Id}");
            if (FailStateChanges && target.Attributes.ContainsKey("statecode"))
                throw Fault(GenericFailure, $"Simulated state change failure for {target.LogicalName} {target.Id}");
            string entity = (target.LogicalName ?? string.Empty).ToLowerInvariant();
            if (target.Attributes.TryGetValue("statecode", out object state) && state is OptionSetValue option
                && ClosingStates.TryGetValue((entity, option.Value), out string closing))
            {
                // The platform's wording (seen live for a won opportunity).
                throw Fault(GenericFailure, $"This message can not be used to set the state of {entity} to {closing}. " +
                                            $"In order to set state of {entity} to {closing}, use the {closing} message instead.");
            }

            foreach (KeyValuePair<string, object> pair in target.Attributes) stored[pair.Key] = pair.Value;
            return new UpdateResponse();
        }

        /// <summary>States a plain Update may not set: the platform wants the close message.</summary>
        private static readonly Dictionary<(string Entity, int State), string> ClosingStates = new Dictionary<(string Entity, int State), string>
        {
            [("opportunity", 1)] = "won", [("opportunity", 2)] = "lost", [("incident", 1)] = "resolved",
            [("quote", 2)] = "won", [("quote", 3)] = "closed", [("salesorder", 2)] = "canceled", [("salesorder", 3)] = "fulfilled"
        };

        /// <summary>The states a close message closes from: an open opportunity, an active incident, an ACTIVE quote, an active or submitted order.</summary>
        private static readonly Dictionary<string, int[]> CloseFromStates = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["opportunity"] = new[] { 0 }, ["incident"] = new[] { 0 }, ["quote"] = new[] { 1 }, ["salesorder"] = new[] { 0, 1 }
        };

        /// <summary>A close message: its close activity must point at a stored record in a state it closes from; that record gets the new state.</summary>
        private OrganizationResponse DoClose(OrganizationRequest request, Entity close, string lookup, int state, OptionSetValue status, OrganizationResponse response)
        {
            if (close == null || !close.Attributes.TryGetValue(lookup, out object value) || !(value is EntityReference record))
                throw Fault(GenericFailure, $"{request.RequestName}: the close activity has no {lookup}.");
            if (!_store.TryGetValue(Key(record.LogicalName, record.Id), out Entity stored))
                throw NotFound(record.LogicalName, record.Id);
            if (FailStateChanges)
                throw Fault(GenericFailure, $"Simulated state change failure for {record.LogicalName} {record.Id}");
            int current = stored.Attributes.TryGetValue("statecode", out object currentValue) && currentValue is OptionSetValue currentOption ? currentOption.Value : 0;
            if (CloseFromStates.TryGetValue(record.LogicalName, out int[] from) && !from.Contains(current))
                throw Fault(GenericFailure, $"Simulated: {request.RequestName} cannot close {record.LogicalName} {record.Id} from state {current}.");

            stored["statecode"] = new OptionSetValue(state);
            stored["statuscode"] = status == null ? null : new OptionSetValue(status.Value);
            return response;
        }

        /// <summary>
        /// Associates the target with each related record over a declared N:N relationship, as a new row of
        /// its intersect entity. Like the platform: both records must exist, the target must be on one side
        /// of it (a self-referential relationship needs PrimaryEntityRole: Referencing puts the target on
        /// side 1, Referenced on side 2), and a pair that is associated already fails with "Cannot insert
        /// duplicate key.".
        /// </summary>
        private OrganizationResponse DoAssociate(AssociateRequest request)
        {
            string schemaName = request.Relationship?.SchemaName;
            if (string.IsNullOrEmpty(schemaName) || !ManyToManyRelationships.TryGetValue(schemaName, out ManyToManyRelationship relationship))
                throw Fault(GenericFailure, $"Simulated: no N:N relationship named {schemaName}.");
            if (FailAssociates.Contains(schemaName))
                throw Fault(GenericFailure, $"Simulated associate failure for {schemaName}");
            EntityReference target = request.Target ?? throw Fault(GenericFailure, "Associate: no target.");
            if (!_store.ContainsKey(Key(target.LogicalName, target.Id))) throw NotFound(target.LogicalName, target.Id);

            bool targetIsSide1;
            if (relationship.IsSelfReferential)
            {
                EntityRole role = request.Relationship.PrimaryEntityRole
                    ?? throw Fault(GenericFailure, $"Simulated: the PrimaryEntityRole of the self-referential relationship {schemaName} is required.");
                targetIsSide1 = role == EntityRole.Referencing;
            }
            else if (SameEntity(target.LogicalName, relationship.Entity1LogicalName)) targetIsSide1 = true;
            else if (SameEntity(target.LogicalName, relationship.Entity2LogicalName)) targetIsSide1 = false;
            else throw Fault(GenericFailure, $"Simulated: {target.LogicalName} is not a side of {schemaName}.");

            foreach (EntityReference related in request.RelatedEntities ?? new EntityReferenceCollection())
            {
                string expected = targetIsSide1 ? relationship.Entity2LogicalName : relationship.Entity1LogicalName;
                if (!SameEntity(related.LogicalName, expected)) throw Fault(GenericFailure, $"Simulated: {related.LogicalName} is not the other side of {schemaName}.");
                if (!_store.ContainsKey(Key(related.LogicalName, related.Id))) throw NotFound(related.LogicalName, related.Id);
                Guid side1 = targetIsSide1 ? target.Id : related.Id;
                Guid side2 = targetIsSide1 ? related.Id : target.Id;
                if (IntersectRows(relationship).Any(row => Equals(row[relationship.Entity1IntersectAttribute], side1) && Equals(row[relationship.Entity2IntersectAttribute], side2)))
                    throw Fault(DuplicateRecord, "Cannot insert duplicate key.");
                AddIntersectRow(relationship, side1, side2);
            }
            return new AssociateResponse();
        }

        private ManyToManyRelationship DeclaredRelationship(string schemaName) =>
            ManyToManyRelationships.TryGetValue(schemaName ?? string.Empty, out ManyToManyRelationship relationship)
                ? relationship
                : throw new ArgumentException($"Declare the N:N relationship {schemaName} with ManyToMany first.");

        private IEnumerable<Entity> IntersectRows(ManyToManyRelationship relationship) =>
            _store.Values.Where(e => string.Equals(e.LogicalName, relationship.IntersectEntity, StringComparison.OrdinalIgnoreCase));

        private void AddIntersectRow(ManyToManyRelationship relationship, Guid side1, Guid side2)
        {
            var row = new Entity(relationship.IntersectEntity.ToLowerInvariant(), Guid.NewGuid());
            row[relationship.Entity1IntersectAttribute] = side1;
            row[relationship.Entity2IntersectAttribute] = side2;
            _store[Key(row.LogicalName, row.Id)] = row;
        }

        private static bool SameEntity(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        private OrganizationResponse DoRetrieve(EntityReference target, ColumnSet columns)
        {
            if (FailRetrieves.TryGetValue(target.LogicalName + "/" + target.Id.ToString("D"), out Exception failure)
                || FailRetrieves.TryGetValue(target.LogicalName ?? string.Empty, out failure))
            {
                throw failure;
            }
            if (!_store.TryGetValue(Key(target.LogicalName, target.Id), out Entity stored))
                throw NotFound(target.LogicalName, target.Id);

            var response = new RetrieveResponse();
            response.Results["Entity"] = Project(stored, columns);
            return response;
        }

        private OrganizationResponse DoRetrieveMultiple(QueryBase query)
        {
            if (query is QueryExpression failing && FailRetrieveMultiple.TryGetValue(failing.EntityName ?? string.Empty, out Exception failure))
                throw failure;

            EntityCollection collection = RetrieveMultipleHandler?.Invoke(query);
            if (collection == null)
            {
                if (query is QueryExpression expression) collection = Evaluate(expression);
                else if (RetrieveMultipleHandler != null) collection = new EntityCollection();
                else throw new NotSupportedException($"Set RetrieveMultipleHandler to answer a {query?.GetType().Name}.");
            }

            var response = new RetrieveMultipleResponse();
            response.Results["EntityCollection"] = collection;
            return response;
        }

        /// <summary>
        /// Supports AND-ed Equal / NotNull / Null conditions, orders (by the stored values; the primary id
        /// by the record id), ColumnSet, TopCount and paging: with PageInfo.Count set, page PageNumber of
        /// that size, MoreRecords, and a paging cookie "&lt;cookie page="N" /&gt;".
        /// </summary>
        private EntityCollection Evaluate(QueryExpression query)
        {
            if (query.Criteria.Filters.Count > 0 || query.LinkEntities.Count > 0)
                throw new NotSupportedException("Nested filters and link-entities are not supported by the fake.");

            IEnumerable<Entity> matches = _store.Values
                .Where(e => string.Equals(e.LogicalName, query.EntityName, StringComparison.OrdinalIgnoreCase))
                .Where(e => query.Criteria.Conditions.All(c => ConditionMatches(e, c)));
            IOrderedEnumerable<Entity> ordered = null;
            foreach (OrderExpression order in query.Orders)
            {
                Func<Entity, object> key = e => IsPrimaryId(e.LogicalName, order.AttributeName)
                    ? e.Id
                    : e.Attributes.TryGetValue(order.AttributeName, out object value) ? SortValue(value) : null;
                bool descending = order.OrderType == OrderType.Descending;
                ordered = ordered == null
                    ? (descending ? matches.OrderByDescending(key, Comparer<object>.Default) : matches.OrderBy(key, Comparer<object>.Default))
                    : (descending ? ordered.ThenByDescending(key, Comparer<object>.Default) : ordered.ThenBy(key, Comparer<object>.Default));
            }
            List<Entity> rows = (ordered ?? matches).Select(e => Project(e, query.ColumnSet)).ToList();
            if (query.TopCount.HasValue) rows = rows.Take(query.TopCount.Value).ToList();

            PagingInfo paging = query.PageInfo;
            if (paging == null || paging.Count <= 0) return new EntityCollection(rows) { EntityName = query.EntityName };
            int page = Math.Max(1, paging.PageNumber);
            return new EntityCollection(rows.Skip((page - 1) * paging.Count).Take(paging.Count).ToList())
            {
                EntityName = query.EntityName,
                MoreRecords = rows.Count > page * paging.Count,
                PagingCookie = $"<cookie page=\"{page}\" />"
            };
        }

        private static object SortValue(object value)
        {
            switch (value)
            {
                case EntityReference reference: return reference.Id;
                case OptionSetValue option: return option.Value;
                case Money money: return money.Value;
                default: return value;
            }
        }

        private bool ConditionMatches(Entity entity, ConditionExpression condition)
        {
            object actual = IsPrimaryId(entity.LogicalName, condition.AttributeName)
                ? entity.Id
                : entity.Attributes.TryGetValue(condition.AttributeName, out object value) ? value : null;

            switch (condition.Operator)
            {
                case ConditionOperator.Equal:
                    return condition.Values.Count > 0 && ValuesEqual(actual, condition.Values[0]);
                case ConditionOperator.NotNull:
                    return actual != null;
                case ConditionOperator.Null:
                    return actual == null;
                default:
                    throw new NotSupportedException($"Condition operator {condition.Operator} is not supported by the fake.");
            }
        }

        private static bool ValuesEqual(object actual, object expected)
        {
            switch (actual)
            {
                case null: return expected == null;
                case EntityReference reference: return Equals(reference.Id, expected);
                case OptionSetValue option: return Equals(option.Value, expected);
                case string text: return string.Equals(text, expected as string, StringComparison.OrdinalIgnoreCase);
                default: return Equals(actual, expected);
            }
        }

        private Entity Project(Entity stored, ColumnSet columns)
        {
            var copy = new Entity(stored.LogicalName) { Id = stored.Id };
            if (columns == null || columns.AllColumns)
            {
                foreach (KeyValuePair<string, object> pair in stored.Attributes) copy[pair.Key] = pair.Value;
                return copy;
            }
            foreach (string column in columns.Columns)
            {
                if (stored.Attributes.TryGetValue(column, out object value)) copy[column] = value;
                else if (IsPrimaryId(stored.LogicalName, column)) copy[column] = stored.Id;
            }
            return copy;
        }

        private bool IsPrimaryId(string entity, string attribute)
        {
            string primaryId = PrimaryIdAttributes.TryGetValue(entity, out string mapped) ? mapped : entity + "id";
            return string.Equals(attribute, primaryId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(HashSet<string> set, string entity, Guid id) =>
            set.Contains(entity) || set.Contains(entity + "/" + id.ToString("D"));

        private static (string Entity, Guid Id) Key(string entity, Guid id) => ((entity ?? string.Empty).ToLowerInvariant(), id);

        private static FaultException<OrganizationServiceFault> NotFound(string entity, Guid id) =>
            Fault(ObjectDoesNotExist, $"Entity '{entity}' With Id = {id} Does Not Exist");

        internal static Entity Clone(Entity entity)
        {
            var copy = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (KeyValuePair<string, object> pair in entity.Attributes) copy[pair.Key] = pair.Value;
            return copy;
        }
    }
}
