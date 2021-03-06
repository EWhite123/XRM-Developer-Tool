#region

using JosephM.Core.Extentions;
using JosephM.Core.FieldType;
using JosephM.Core.Log;
using JosephM.Core.Service;
using JosephM.Core.Utility;
using JosephM.Record.Extentions;
using JosephM.Record.IService;
using JosephM.Record.Xrm.XrmRecord;
using JosephM.Xrm;
using JosephM.Xrm.Schema;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

#endregion

namespace JosephM.Deployment
{
    public abstract class DataImportServiceBase<TReq, Tres, TResItem>
        : ServiceBase<TReq, Tres, TResItem>
        where TReq : ServiceRequestBase
        where Tres : ServiceResponseBase<TResItem>, new()
        where TResItem : ServiceResponseItem
    {
        public DataImportServiceBase(XrmRecordService xrmRecordService)
        {
            XrmRecordService = xrmRecordService;
        }

        public XrmRecordService XrmRecordService { get; set; }

        protected XrmService XrmService
        {
            get
            {
                return XrmRecordService.XrmService;
            }
        }

        public string GetBaseTransactionId(IRecordService service)
        {
            var organisation = service.GetFirst("organization");
            return organisation.GetLookupId("basecurrencyid");
        }

        protected IEnumerable<Entity> GetMatchingEntities(string type, IDictionary<string,object> fieldValues)
        {
            var conditions = fieldValues.Select(fv =>
            fv.Value == null
            ? new ConditionExpression(fv.Key, ConditionOperator.Null)
            : new ConditionExpression(fv.Key, ConditionOperator.Equal, XrmService.ConvertToQueryValue(fv.Key, type, XrmService.ParseField(fv.Key, type, fv.Value)))
            ).ToList();
            if (type == "workflow")
                conditions.Add(new ConditionExpression("type", ConditionOperator.Equal, XrmPicklists.WorkflowType.Definition));
            if (type == "account" || type == "contact")
                conditions.Add(new ConditionExpression("merged", ConditionOperator.NotEqual, true));
            if (type == "knowledgearticle")
                conditions.Add(new ConditionExpression("islatestversion", ConditionOperator.Equal, true));
            return XrmService.RetrieveAllAndClauses(type, conditions, new String[0]);
        }

        protected IEnumerable<Entity> GetMatchingEntities(string type, string field, string value)
        {
            return GetMatchingEntities(type, new Dictionary<string, object>()
            {
                { field, value }
            });
        }

        private Entity GetUniqueMatchingEntity(string type, string field, string value)
        {
            var matchingRecords = GetMatchingEntities(type, field, value);
            if (!matchingRecords.Any())
                throw new NullReferenceException(string.Format("No Record Matched To The {0} Of {1} When Matching The Name",
                        "Name", value));
            if (matchingRecords.Count() > 1)
                throw new Exception(string.Format("More Than One Record Match To The {0} Of {1} When Matching The Name",
                    "Name", value));
            return matchingRecords.First();
        }

        private string MapColumnToFieldSchemaName(XrmService service, string type, string column)
        {
            if (column.StartsWith("key|"))
                column = column.Substring(4);
            var fields = service.GetFields(type);
            var fieldsForLabel = fields.Where(f => service.GetFieldLabel(f, type) == column);
            if (fieldsForLabel.Count() == 1)
                return fieldsForLabel.First();
            var fieldsForName = fields.Where(t => t.ToLower() == column.ToLower());
            if (fieldsForName.Any())
                return fieldsForName.First();
            throw new NullReferenceException(string.Format("No Unique Field Found On Record Type {0} Matched (Label Or Name) For Column Of {1}", type, column));
        }

        private class GetTargetTypeResponse
        {
            public GetTargetTypeResponse(string logicalName, bool isRelationship)
            {
                LogicalName = logicalName;
                IsRelationship = isRelationship;
            }

            public bool IsRelationship { get; set; }
            public string LogicalName { get; set; }
        }

        private GetTargetTypeResponse GetTargetType(XrmService service, string csvName)
        {
            var name = csvName;
            if (name.EndsWith(".csv"))
                name = name.Substring(0, name.IndexOf(".csv", StringComparison.Ordinal));
            name = Path.GetFileName(name);
            var recordTypes = service.GetAllEntityTypes();
            var typesForLabel = recordTypes.Where(t => service.GetEntityDisplayName(t) == name || service.GetEntityCollectionName(t) == name);
            if (typesForLabel.Count() == 1)
                return new GetTargetTypeResponse(typesForLabel.First(), false);
            var typesForName = recordTypes.Where(t => t == name);
            if (typesForName.Any())
                return new GetTargetTypeResponse(typesForName.First(), false);

            var relationshipEntities = service.GetAllNnRelationshipEntityNames();
            var matchingRelationships = relationshipEntities.Where(r => r == name);
            if (matchingRelationships.Count() == 1)
                return new GetTargetTypeResponse(matchingRelationships.First(), true);

            throw new NullReferenceException(string.Format("No Unique Record Type Or Relationship Matched (Label Or Name) For CSV Name Of {0}", name));
        }

        public IEnumerable<DataImportResponseItem> DoImport(IEnumerable<Entity> entities, LogController controller, bool maskEmails)
        {
            controller.LogLiteral("Preparing Import");
            var response = new List<DataImportResponseItem>();

            var fieldsToRetry = new Dictionary<Entity, List<string>>();
            var typesToImport = entities.Select(e => e.LogicalName).Distinct();

            var allNNRelationships = XrmService.GetAllNnRelationshipEntityNames();
            var associationTypes = typesToImport.Where(allNNRelationships.Contains).ToArray();

            typesToImport = typesToImport.Where(t => !associationTypes.Contains(t)).ToArray();

            var orderedTypes = new List<string>();

            var idSwitches = new Dictionary<string, Dictionary<Guid, Guid>>();
            foreach (var item in typesToImport)
                idSwitches.Add(item, new Dictionary<Guid, Guid>());

            #region tryordertypes

            //lets put team first because some other records
            //may reference the queue which only gets created
            //when the team does
            typesToImport = typesToImport.OrderBy(s => s == Entities.team ? 0 : 1).ToArray();

            foreach (var type in typesToImport)
            {
                //iterate through the types and if any of them have a lookup which references this type
                //then insert this one before it for import first
                //otherwise just append to the end
                foreach (var type2 in orderedTypes)
                {
                    var thatType = type2;
                    var thatTypeEntities = entities.Where(e => e.LogicalName == thatType).ToList();
                    var fields = GetFieldsToImport(thatTypeEntities, thatType)
                        .Where(f => XrmService.FieldExists(f, thatType) && XrmService.IsLookup(f, thatType));

                    foreach (var field in fields)
                    {
                        if (thatTypeEntities.Any(e => e.GetLookupType(field) == type))
                        {
                            orderedTypes.Insert(orderedTypes.IndexOf(type2), type);
                            break;
                        }
                    }
                    if (orderedTypes.Contains(type))
                        break;
                }
                if (!orderedTypes.Contains(type))
                    orderedTypes.Add(type);
            }

            #endregion tryordertypes
            var estimator = new TaskEstimator(1);

            var countToImport = orderedTypes.Count;
            var countImported = 0;
            foreach (var recordType in orderedTypes)
            {
                try
                {
                    var thisRecordType = recordType;
                    var displayPrefix = $"Importing {recordType} Records ({countImported + 1}/{countToImport})";
                    controller.UpdateProgress(countImported++, countToImport, string.Format("Importing {0} Records", recordType));
                    controller.UpdateLevel2Progress(0, 1, "Loading");
                    var primaryField = XrmService.GetPrimaryNameField(recordType);
                    var thisTypeEntities = entities.Where(e => e.LogicalName == recordType).ToList();

                    var orConditions = thisTypeEntities
                        .Select(
                            e =>
                                new ConditionExpression(XrmService.GetPrimaryKeyField(e.LogicalName),
                                    ConditionOperator.Equal, e.Id))
                        .ToArray();
                    var existingEntities = XrmService.RetrieveAllOrClauses(recordType, orConditions);

                    var orderedEntities = new List<Entity>();

                    #region tryorderentities

                    var importFieldsForEntity = GetFieldsToImport(thisTypeEntities, recordType).ToArray();
                    var fieldsDontExist = GetFieldsInEntities(thisTypeEntities)
                        .Where(f => !XrmService.FieldExists(f, thisRecordType))
                        .Where(f => !HardcodedIgnoreFields.Contains(f))
                        .Distinct()
                        .ToArray();
                    foreach (var field in fieldsDontExist)
                    {
                        response.Add(
                                new DataImportResponseItem(recordType, field, null,
                                string.Format("Field {0} On Entity {1} Doesn't Exist In Target Instance And Will Be Ignored", field, recordType),
                                new NullReferenceException(string.Format("Field {0} On Entity {1} Doesn't Exist In Target Instance And Will Be Ignored", field, recordType))));
                    }

                    var selfReferenceFields = importFieldsForEntity.Where(
                                f =>
                                    XrmService.IsLookup(f, recordType) &&
                                    XrmService.GetLookupTargetEntity(f, recordType) == recordType).ToArray();

                    foreach (var entity in thisTypeEntities)
                    {
                        foreach (var entity2 in orderedEntities)
                        {
                            if (selfReferenceFields.Any(f => entity2.GetLookupGuid(f) == entity.Id || (entity2.GetLookupGuid(f) == Guid.Empty && entity2.GetLookupName(f) == entity.GetStringField(primaryField))))
                            {
                                orderedEntities.Insert(orderedEntities.IndexOf(entity2), entity);
                                break;
                            }
                        }
                        if (!orderedEntities.Contains(entity))
                            orderedEntities.Add(entity);
                    }

                    #endregion tryorderentities

                    var countRecordsToImport = orderedEntities.Count;
                    var countRecordsImported = 0;
                    estimator = new TaskEstimator(countRecordsToImport);

                    foreach (var entity in orderedEntities)
                    {
                        var thisEntity = entity;
                        try
                        {
                            var existingMatchingIds = GetMatchForExistingRecord(existingEntities, thisEntity);
                            if (existingMatchingIds.Any())
                            {
                                var matchRecord = existingMatchingIds.First();
                                idSwitches[recordType].Add(thisEntity.Id, matchRecord.Id);
                                thisEntity.Id = matchRecord.Id;
                                thisEntity.SetField(XrmService.GetPrimaryKeyField(thisEntity.LogicalName), thisEntity.Id);
                            }
                            var isUpdate = existingMatchingIds.Any();
                            foreach (var field in thisEntity.GetFieldsInEntity().ToArray())
                            {
                                if (importFieldsForEntity.Contains(field) &&
                                    XrmService.IsLookup(field, thisEntity.LogicalName) &&
                                    thisEntity.GetField(field) != null)
                                {
                                    var idNullable = thisEntity.GetLookupGuid(field);
                                    if (idNullable.HasValue)
                                    {
                                        var targetTypesToTry = GetTargetTypesToTry(thisEntity, field);
                                        var name = thisEntity.GetLookupName(field);
                                        var fieldResolved = false;
                                        foreach (var lookupEntity in targetTypesToTry)
                                        {
                                            var targetPrimaryKey = XrmRecordService.GetPrimaryKey(lookupEntity);
                                            var targetPrimaryField = XrmRecordService.GetPrimaryField(lookupEntity);
                                            var matchRecord = XrmService.GetFirst(lookupEntity, targetPrimaryKey,
                                                idNullable.Value);
                                            if (matchRecord != null)
                                            {
                                                thisEntity.SetLookupField(field, matchRecord);
                                                fieldResolved = true;
                                            }
                                            else
                                            {
                                                var matchRecords = name.IsNullOrWhiteSpace() ?
                                                    new Entity[0] :
                                                    GetMatchingEntities(lookupEntity,
                                                    targetPrimaryField,
                                                    name);
                                                if (matchRecords.Count() == 1)
                                                {
                                                    thisEntity.SetLookupField(field, matchRecords.First());
                                                    fieldResolved = true;
                                                }
                                            }
                                            if (!fieldResolved)
                                            {
                                                if (!fieldsToRetry.ContainsKey(thisEntity))
                                                    fieldsToRetry.Add(thisEntity, new List<string>());
                                                fieldsToRetry[thisEntity].Add(field);
                                            }
                                        }
                                    }
                                }
                            }
                            var fieldsToSet = new List<string>();
                            fieldsToSet.AddRange(thisEntity.GetFieldsInEntity()
                                .Where(importFieldsForEntity.Contains));
                            if (fieldsToRetry.ContainsKey(thisEntity))
                                fieldsToSet.RemoveAll(f => fieldsToRetry[thisEntity].Contains(f));

                            if (maskEmails)
                            {
                                var emailFields = new[] { "emailaddress1", "emailaddress2", "emailaddress3" };
                                foreach (var field in emailFields)
                                {
                                    var theEmail = thisEntity.GetStringField(field);
                                    if (!string.IsNullOrWhiteSpace(theEmail))
                                    {
                                        thisEntity.SetField(field, theEmail.Replace("@", "_AT_") + "_fakecrmdevemail@example.com");
                                    }
                                }
                            }

                            if (isUpdate)
                            {
                                var existingRecord = existingMatchingIds.First();
                                XrmService.Update(thisEntity, fieldsToSet.Where(f => !XrmEntity.FieldsEqual(existingRecord.GetField(f), thisEntity.GetField(f))));
                            }
                            else
                            {
                                PopulateRequiredCreateFields(fieldsToRetry, thisEntity, fieldsToSet);
                                CheckThrowValidForCreate(thisEntity, fieldsToSet);
                                thisEntity.Id = XrmService.Create(thisEntity, fieldsToSet);
                            }
                            if (!isUpdate && thisEntity.GetOptionSetValue("statecode") > 0)
                                XrmService.SetState(thisEntity, thisEntity.GetOptionSetValue("statecode"), thisEntity.GetOptionSetValue("statuscode"));
                            else if (isUpdate && existingMatchingIds.Any())
                            {
                                var matchRecord = existingMatchingIds.First();
                                var thisState = thisEntity.GetOptionSetValue("statecode");
                                var thisStatus = thisEntity.GetOptionSetValue("statuscode");
                                var matchState = matchRecord.GetOptionSetValue("statecode");
                                var matchStatus = matchRecord.GetOptionSetValue("statuscode");
                                if ((thisState != -1 && thisState != matchState)
                                    ||  (thisStatus != -1 && thisState != matchStatus))
                                {
                                    XrmService.SetState(thisEntity, thisEntity.GetOptionSetValue("statecode"), thisEntity.GetOptionSetValue("statuscode"));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (fieldsToRetry.ContainsKey(thisEntity))
                                fieldsToRetry.Remove(thisEntity);
                            response.Add(
                                new DataImportResponseItem(recordType, null, entity.GetStringField(primaryField),
                                    string.Format("Error Importing Record Id={0}", entity.Id),
                                    ex));
                        }
                        countRecordsImported++;
                        controller.UpdateLevel2Progress(countRecordsImported, countRecordsToImport, estimator.GetProgressString(countRecordsImported));
                    }
                }
                catch (Exception ex)
                {
                    response.Add(
                        new DataImportResponseItem(recordType, null, null, string.Format("Error Importing Type {0}", recordType), ex));
                }
            }
            controller.TurnOffLevel2();
            countToImport = fieldsToRetry.Count;
            countImported = 0;
            controller.UpdateProgress(countImported, countToImport, "Retrying Unresolved Fields");
            estimator = new TaskEstimator(countToImport);
            foreach (var item in fieldsToRetry)
            {
                var thisEntity = item.Key;
                controller.UpdateProgress(countImported++, countToImport, string.Format("Retrying Unresolved Fields {0}", thisEntity.LogicalName));
                var thisPrimaryField = XrmService.GetPrimaryNameField(thisEntity.LogicalName);
                try
                {
                    foreach (var field in item.Value)
                    {
                        if (XrmService.IsLookup(field, thisEntity.LogicalName) && thisEntity.GetField(field) != null)
                        {
                            try
                            {
                                var targetTypesToTry = GetTargetTypesToTry(thisEntity, field);
                                var name = thisEntity.GetLookupName(field);
                                var idNullable = thisEntity.GetLookupGuid(field);
                                var fieldResolved = false;
                                foreach (var lookupEntity in targetTypesToTry)
                                {
                                    var targetPrimaryKey = XrmRecordService.GetPrimaryKey(lookupEntity);
                                    var targetPrimaryField = XrmRecordService.GetPrimaryField(lookupEntity);
                                    var matchRecord = idNullable.HasValue ? XrmService.GetFirst(lookupEntity, targetPrimaryKey,
                                        idNullable.Value) : null;
                                    if (matchRecord != null)
                                    {
                                        thisEntity.SetLookupField(field, matchRecord);
                                        fieldResolved = true;
                                    }
                                    else
                                    {
                                        var matchRecords = name.IsNullOrWhiteSpace() ?
                                            new Entity[0] :
                                            GetMatchingEntities(lookupEntity,
                                            targetPrimaryField,
                                            name);
                                        if (matchRecords.Count() == 1)
                                        {
                                            thisEntity.SetLookupField(field, matchRecords.First());
                                            fieldResolved = true;
                                        }
                                    }
                                    if (!fieldResolved)
                                    {
                                        throw new Exception(string.Format("Could not find matching record for field {0}.{1} {2}", thisEntity.LogicalName, field, name));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (thisEntity.Contains(field))
                                    thisEntity.Attributes.Remove(field);
                                response.Add(
                                     new DataImportResponseItem(thisEntity.LogicalName, field, thisEntity.GetStringField(thisPrimaryField),
                                        string.Format("Error Setting Lookup Field Id={0}", thisEntity.Id), ex));
                            }
                        }
                    }
                    XrmService.Update(thisEntity, item.Value);
                }
                catch (Exception ex)
                {
                    response.Add(
                        new DataImportResponseItem(thisEntity.LogicalName, null, thisEntity.GetStringField(thisPrimaryField),
                            string.Format("Error Importing Record Id={0}", thisEntity.Id),
                            ex));
                }
                countImported++;
                controller.UpdateProgress(countImported, countToImport, estimator.GetProgressString(countImported, taskName: $"Retrying Unresolved Fields {thisEntity.LogicalName}"));
            }
            countToImport = associationTypes.Count();
            countImported = 0;
            foreach (var relationshipEntityName in associationTypes)
            {
                var thisEntityName = relationshipEntityName;
                controller.UpdateProgress(countImported++, countToImport, $"Associating {thisEntityName} Records");
                controller.UpdateLevel2Progress(0, 1, "Loading");
                var thisTypeEntities = entities.Where(e => e.LogicalName == thisEntityName).ToList();
                var countRecordsToImport = thisTypeEntities.Count;
                var countRecordsImported = 0;
                estimator = new TaskEstimator(countRecordsToImport);
                foreach (var thisEntity in thisTypeEntities)
                {
                    try
                    {
                        var relationship = XrmService.GetRelationshipMetadataForEntityName(thisEntityName);
                        var type1 = relationship.Entity1LogicalName;
                        var field1 = relationship.Entity1IntersectAttribute;
                        var type2 = relationship.Entity2LogicalName;
                        var field2 = relationship.Entity2IntersectAttribute;

                        //bit of hack
                        //when importing from csv just set the fields to the string name of the referenced record
                        //so either string when csv or guid when xml import/export
                        var value1 = thisEntity.GetField(relationship.Entity1IntersectAttribute);
                        var id1 = value1 is string
                            ? GetUniqueMatchingEntity(type1, XrmRecordService.GetPrimaryField(type1), (string)value1).Id
                            : thisEntity.GetGuidField(relationship.Entity1IntersectAttribute);

                        var value2 = thisEntity.GetField(relationship.Entity2IntersectAttribute);
                        var id2 = value2 is string
                            ? GetUniqueMatchingEntity(type2, XrmRecordService.GetPrimaryField(type2), (string)value2).Id
                            : thisEntity.GetGuidField(relationship.Entity2IntersectAttribute);

                        //add a where field lookup reference then look it up
                        if (idSwitches.ContainsKey(type1) && idSwitches[type1].ContainsKey(id1))
                            id1 = idSwitches[type1][id1];
                        if (idSwitches.ContainsKey(type2) && idSwitches[type2].ContainsKey(id2))
                            id2 = idSwitches[type2][id2];
                        XrmService.AssociateSafe(relationship.SchemaName, type1, field1, id1, type2, field2, new[] { id2 });
                    }
                    catch (Exception ex)
                    {
                        response.Add(
                        new DataImportResponseItem(
                                string.Format("Error Associating Record Of Type {0} Id {1}", thisEntity.LogicalName,
                                    thisEntity.Id),
                                ex));
                    }
                    countRecordsImported++;
                    controller.UpdateLevel2Progress(countRecordsImported, countRecordsToImport, estimator.GetProgressString(countRecordsImported));
                }
            }
            return response;
        }

        private void PopulateRequiredCreateFields(Dictionary<Entity, List<string>> fieldsToRetry, Entity thisEntity, List<string> fieldsToSet)
        {
            if (thisEntity.LogicalName == "team"
                && !fieldsToSet.Contains("businessunitid")
                && XrmService.FieldExists("businessunitid", "team"))
            {
                thisEntity.SetLookupField("businessunitid", GetRootBusinessUnitId(), "businessunit");
                fieldsToSet.Add("businessunitid");
                if (fieldsToRetry.ContainsKey(thisEntity)
                    && fieldsToRetry[thisEntity].Contains("businessunitid"))
                    fieldsToRetry[thisEntity].Remove("businessunitid");
            }
            if (thisEntity.LogicalName == Entities.subject
                    && !fieldsToSet.Contains(Fields.subject_.featuremask)
                    && XrmService.FieldExists(Fields.subject_.featuremask, Entities.subject))
            {
                thisEntity.SetField(Fields.subject_.featuremask, 1);
                fieldsToSet.Add(Fields.subject_.featuremask);
                if (fieldsToRetry.ContainsKey(thisEntity)
                    && fieldsToRetry[thisEntity].Contains(Fields.subject_.featuremask))
                    fieldsToRetry[thisEntity].Remove(Fields.subject_.featuremask);
            }
        }

        private Guid GetRootBusinessUnitId()
        {
            return XrmService.GetFirst("businessunit", "parentbusinessunitid", null, new string[0]).Id;
        }

        private List<string> GetTargetTypesToTry(Entity thisEntity, string field)
        {
            var targetTypesToTry = new List<string>();

            if (!string.IsNullOrWhiteSpace(thisEntity.GetLookupType(field)))
            {
                targetTypesToTry.Add(thisEntity.GetLookupType(field));
            }
            else
            {
                switch (XrmRecordService.GetFieldType(field, thisEntity.LogicalName))
                {
                    case Record.Metadata.RecordFieldType.Owner:
                        targetTypesToTry.Add("systemuser");
                        targetTypesToTry.Add("team");
                        break;
                    case Record.Metadata.RecordFieldType.Customer:
                        targetTypesToTry.Add("account");
                        targetTypesToTry.Add("contact");
                        break;
                    case Record.Metadata.RecordFieldType.Lookup:
                        targetTypesToTry.Add(thisEntity.GetLookupType(field));
                        break;
                    default:
                        throw new NotImplementedException(string.Format("Could not determine target type for field {0}.{1} of type {2}", thisEntity.LogicalName, field, XrmService.GetFieldType(field, thisEntity.LogicalName)));
                }
            }

            return targetTypesToTry;
        }

        private IEnumerable<Entity> GetMatchForExistingRecord(IEnumerable<Entity> existingEntitiesWithIdMatches, Entity thisEntity)
        {
            //this a bit messy
            var existingMatches = existingEntitiesWithIdMatches.Where(e => e.Id == thisEntity.Id);
            if (!existingMatches.Any())
            {
                var matchByNameEntities = new[] { "businessunit", "team", "pricelevel", "uomschedule", "uom", "entitlementtemplate" };
                var matchBySpecificFieldEntities = new Dictionary<string, string>()
                {
                    {  "knowledgearticle", "articlepublicnumber" }
                };
                if (thisEntity.LogicalName == "businessunit" && thisEntity.GetField("parentbusinessunitid") == null)
                {
                    existingMatches = XrmService.RetrieveAllAndClauses("businessunit",
                        new[] { new ConditionExpression("parentbusinessunitid", ConditionOperator.Null) });
                }
                else if (matchByNameEntities.Contains(thisEntity.LogicalName))
                {
                    var primaryField = XrmService.GetPrimaryNameField(thisEntity.LogicalName);
                    var name = thisEntity.GetStringField(primaryField);
                    if (name.IsNullOrWhiteSpace())
                        throw new NullReferenceException(string.Format("{0} Is Required On The {1}", XrmService.GetFieldLabel(primaryField, thisEntity.LogicalName), XrmService.GetEntityLabel(thisEntity.LogicalName)));
                    existingMatches = GetMatchingEntities(thisEntity.LogicalName, primaryField, name);
                    if (existingMatches.Count() > 1)
                        throw new Exception(string.Format("More Than One Record Match To The {0} Of {1} When Matching The Name",
                            "Name", name));
                }
                else if (matchBySpecificFieldEntities.ContainsKey(thisEntity.LogicalName))
                {
                    var matchField = matchBySpecificFieldEntities[thisEntity.LogicalName];
                    var valueToMatch = thisEntity.GetStringField(matchField);
                    if (matchField.IsNullOrWhiteSpace())
                        throw new NullReferenceException(string.Format("{0} Is Required On The {1}", XrmService.GetFieldLabel(matchField, thisEntity.LogicalName), XrmService.GetEntityLabel(thisEntity.LogicalName)));
                    existingMatches = GetMatchingEntities(thisEntity.LogicalName, matchField, valueToMatch);
                    if (existingMatches.Count() > 1)
                        throw new Exception(string.Format("More Than One Record Match To The {0} Of {1}",
                            matchField, valueToMatch));
                }
            }
            return existingMatches;
        }

        private void CheckThrowValidForCreate(Entity thisEntity, List<string> fieldsToSet)
        {
            if (thisEntity != null)
            {
                switch (thisEntity.LogicalName)
                {
                    case "annotation":
                        if (!fieldsToSet.Contains("objectid"))
                            throw new NullReferenceException(string.Format("Cannot create {0} {1} as its parent {2} does not exist"
                                , XrmService.GetEntityLabel(thisEntity.LogicalName), thisEntity.GetStringField(XrmService.GetPrimaryNameField(thisEntity.LogicalName))
                                , thisEntity.GetStringField("objecttypecode") != null ? XrmService.GetEntityLabel(thisEntity.GetStringField("objecttypecode")) : "Unknown Type"));
                        break;
                    case "productpricelevel":
                        if (!fieldsToSet.Contains("pricelevelid"))
                            throw new NullReferenceException(string.Format("Cannot create {0} {1} as its parent {2} is empty"
                                , XrmService.GetEntityLabel(thisEntity.LogicalName), thisEntity.GetStringField(XrmService.GetPrimaryNameField(thisEntity.LogicalName))
                                , XrmService.GetEntityLabel("pricelevel")));
                        break;
                }
            }
            return;
        }

        private IEnumerable<string> GetFieldsInEntities(IEnumerable<Entity> thisTypeEntities)
        {
            return thisTypeEntities.SelectMany(e => e.GetFieldsInEntity());
        }

        private IEnumerable<string> GetFieldsToImport(IEnumerable<Entity> thisTypeEntities, string type)
        {
            var fields = GetFieldsInEntities(thisTypeEntities)
                .Where(f => IsIncludeField(f, type))
                .Distinct();
            return fields;
        }

        public bool IsIncludeField(string fieldName, string entityType)
        {
            var hardcodeInvalidFields = HardcodedIgnoreFields;
            if (hardcodeInvalidFields.Contains(fieldName))
                return false;
            //these are just hack since they are not updateable fields (IsWriteable)
            if (fieldName == "parentbusinessunitid")
                return true;
            if (fieldName == "businessunitid")
                return true;
            if (fieldName == "pricelevelid")
                return true;
            if (fieldName == "salesliteratureid")
                return true;
            return
                XrmRecordService.FieldExists(fieldName, entityType) && XrmRecordService.GetFieldMetadata(fieldName, entityType).Writeable;

        }

        private static IEnumerable<string> HardcodedIgnoreFields
        {
            get
            {
                return new[]
                {
                    "yomifullname", "administratorid", "owneridtype", "ownerid", "timezoneruleversionnumber", "utcconversiontimezonecode", "organizationid", "owninguser", "owningbusinessunit","owningteam",
                    "overriddencreatedon", "statuscode", "statecode", "createdby", "createdon", "modifiedby", "modifiedon", "modifiedon", "jmcg_currentnumberposition", "calendarrules", "parentarticlecontentid", "rootarticleid", "previousarticlecontentid"
                };
            }
        }
    }
}
