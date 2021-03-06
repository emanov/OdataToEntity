﻿using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using OdataToEntity.Parsers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdataToEntity.Parsers
{
    public readonly struct OePostParser
    {
        private readonly IEdmModel _edmModel;
        private readonly Db.OeDataAdapter _dataAdapter;

        public OePostParser(IEdmModel edmModel)
        {
            _edmModel = edmModel;
            _dataAdapter = edmModel.GetDataAdapter(edmModel.EntityContainer);
        }

        public OeQueryContext CreateQueryContext(ODataUri odataUri, OeMetadataLevel metadataLevel)
        {
            var importSegment = (OperationImportSegment)odataUri.Path.FirstSegment;
            IEdmEntitySet entitySet = GetEntitySet(importSegment.OperationImports.Single());
            if (entitySet == null)
                return null;

            Type clrType = _edmModel.GetClrType(entitySet.EntityType());
            OePropertyAccessor[] accessors = OePropertyAccessor.CreateFromType(clrType, entitySet);

            Db.OeEntitySetAdapter entitySetAdapter = _dataAdapter.EntitySetAdapters.FindByEntitySet(entitySet);
            return new OeQueryContext(_edmModel, odataUri, null, false, 0, false, metadataLevel, entitySetAdapter)
            {
                EntryFactory = OeEntryFactory.CreateEntryFactory(entitySet, accessors),
            };
        }
        public async Task ExecuteAsync(ODataUri odataUri, Stream requestStream, OeRequestHeaders headers, Stream responseStream, CancellationToken cancellationToken)
        {
            Object dataContext = null;
            try
            {
                dataContext = _dataAdapter.CreateDataContext();
                using (Db.OeAsyncEnumerator asyncEnumerator = GetAsyncEnumerator(odataUri, requestStream, headers, dataContext, out bool isScalar))
                {
                    if (isScalar)
                    {
                        if (await asyncEnumerator.MoveNextAsync().ConfigureAwait(false) && asyncEnumerator.Current != null)
                        {
                            headers.ResponseContentType = OeRequestHeaders.TextDefault.ContentType;
                            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(asyncEnumerator.Current.ToString());
                            responseStream.Write(buffer, 0, buffer.Length);
                        }
                        else
                            headers.ResponseContentType = null;
                    }
                    else
                    {
                        OeQueryContext queryContext = CreateQueryContext(odataUri, headers.MetadataLevel);
                        if (queryContext == null)
                            await WriteCollectionAsync(_edmModel, odataUri, asyncEnumerator, responseStream).ConfigureAwait(false);
                        else
                            await Writers.OeGetWriter.SerializeAsync(queryContext, asyncEnumerator, headers.ContentType, responseStream).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (dataContext != null)
                    _dataAdapter.CloseDataContext(dataContext);
            }
        }
        private void FillParameters(List<KeyValuePair<String, Object>> parameters, Stream requestStream, IEdmOperation operation, String contentType)
        {
            if (!operation.Parameters.Any())
                return;

            IODataRequestMessage requestMessage = new Infrastructure.OeInMemoryMessage(requestStream, contentType);
            var settings = new ODataMessageReaderSettings() { EnableMessageStreamDisposal = false };
            using (var messageReader = new ODataMessageReader(requestMessage, settings, _edmModel))
            {
                ODataParameterReader parameterReader = messageReader.CreateODataParameterReader(operation);
                while (parameterReader.Read())
                {
                    Object value;
                    switch (parameterReader.State)
                    {
                        case ODataParameterReaderState.Value:
                            {
                                value = OeEdmClrHelper.GetValue(_edmModel, parameterReader.Value);
                                break;
                            }
                        case ODataParameterReaderState.Collection:
                            {
                                ODataCollectionReader collectionReader = parameterReader.CreateCollectionReader();
                                value = OeEdmClrHelper.GetValue(_edmModel, ReadCollection(collectionReader));
                                break;
                            }
                        case ODataParameterReaderState.Resource:
                            {
                                ODataReader reader = parameterReader.CreateResourceReader();
                                value = OeEdmClrHelper.GetValue(_edmModel, ReadResource(reader));
                                break;
                            }
                        case ODataParameterReaderState.ResourceSet:
                            {
                                ODataReader reader = parameterReader.CreateResourceSetReader();
                                value = OeEdmClrHelper.GetValue(_edmModel, ReadResourceSet(reader));
                                break;
                            }
                        default:
                            continue;
                    }

                    parameters.Add(new KeyValuePair<String, Object>(parameterReader.Name, value));
                }
            }
        }
        public Db.OeAsyncEnumerator GetAsyncEnumerator(ODataUri odataUri, Stream requestStream, OeRequestHeaders headers, Object dataContext, out bool isScalar)
        {
            isScalar = true;
            var importSegment = (OperationImportSegment)odataUri.Path.LastSegment;
            List<KeyValuePair<String, Object>> parameters = GetParameters(importSegment, odataUri.ParameterAliasNodes, requestStream, headers.ContentType);

            IEdmOperationImport operationImport = importSegment.OperationImports.Single();
            IEdmEntitySet entitySet = GetEntitySet(operationImport);
            if (entitySet == null)
            {
                if (operationImport.Operation.ReturnType == null)
                    return _dataAdapter.OperationAdapter.ExecuteProcedureNonQuery(dataContext, operationImport.Name, parameters);

                Type returnType = _edmModel.GetClrType(operationImport.Operation.ReturnType.Definition);
                if (operationImport.Operation.ReturnType.IsCollection())
                {
                    isScalar = false;
                    returnType = typeof(IEnumerable<>).MakeGenericType(returnType);
                }

                if (_edmModel.IsDbFunction(operationImport.Operation))
                    return _dataAdapter.OperationAdapter.ExecuteFunctionPrimitive(dataContext, operationImport.Name, parameters, returnType);
                else
                    return _dataAdapter.OperationAdapter.ExecuteProcedurePrimitive(dataContext, operationImport.Name, parameters, returnType);
            }

            isScalar = false;
            Db.OeEntitySetAdapter entitySetAdapter = _dataAdapter.EntitySetAdapters.FindByEntitySet(entitySet);
            if (_edmModel.IsDbFunction(operationImport.Operation))
                return _dataAdapter.OperationAdapter.ExecuteFunctionReader(dataContext, operationImport.Name, parameters, entitySetAdapter);
            else
                return _dataAdapter.OperationAdapter.ExecuteProcedureReader(dataContext, operationImport.Name, parameters, entitySetAdapter);
        }
        private IEdmEntitySet GetEntitySet(IEdmOperationImport operationImport)
        {
            if (operationImport.EntitySet is IEdmPathExpression path)
                return _edmModel.FindDeclaredEntitySet(String.Join(".", path.PathSegments));

            return null;
        }
        private List<KeyValuePair<String, Object>> GetParameters(OperationImportSegment importSegment, IDictionary<string, SingleValueNode> parameterAliasNodes, Stream requestStream, String contentType)
        {
            var parameters = new List<KeyValuePair<String, Object>>();

            foreach (OperationSegmentParameter segmentParameter in importSegment.Parameters)
            {
                Object value;
                if (segmentParameter.Value is ConstantNode constantNode)
                    value = OeEdmClrHelper.GetValue(_edmModel, constantNode.Value);
                else if (segmentParameter.Value is ParameterAliasNode parameterAliasNode)
                {
                    value = ((ConstantNode)parameterAliasNodes[parameterAliasNode.Alias]).Value;
                    if (value is ODataCollectionValue collectionValue)
                        value = collectionValue.Items;
                }
                else
                    value = OeEdmClrHelper.GetValue(_edmModel, segmentParameter.Value);
                parameters.Add(new KeyValuePair<String, Object>(segmentParameter.Name, value));
            }

            var operation = (EdmOperation)importSegment.OperationImports.Single().Operation;
            if (parameters.Count == 0 && requestStream != null)
                FillParameters(parameters, requestStream, operation, contentType);
            OrderParameters(operation.Parameters, parameters);

            return parameters;
        }
        private static void OrderParameters(IEnumerable<IEdmOperationParameter> operationParameters, List<KeyValuePair<String, Object>> parameters)
        {
            int pos = 0;
            foreach (IEdmOperationParameter operationParameter in operationParameters)
            {
                for (int i = pos; i < parameters.Count; i++)
                    if (String.Compare(operationParameter.Name, parameters[i].Key, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        if (i != pos)
                        {
                            KeyValuePair<String, Object> temp = parameters[pos];
                            parameters[pos] = parameters[i];
                            parameters[i] = temp;
                        }
                        pos++;
                        break;
                    }
            }
        }
        private static ODataCollectionValue ReadCollection(ODataCollectionReader collectionReader)
        {
            var items = new List<Object>();
            while (collectionReader.Read())
            {
                if (collectionReader.State == ODataCollectionReaderState.Completed)
                    break;

                if (collectionReader.State == ODataCollectionReaderState.Value)
                    items.Add(collectionReader.Item);
            }

            return new ODataCollectionValue() { Items = items };
        }
        private static ODataResource ReadResource(ODataReader reader)
        {
            ODataResource resource = null;
            while (reader.Read())
                if (reader.State == ODataReaderState.ResourceEnd)
                    resource = (ODataResource)reader.Item;
            return resource;
        }
        private static ODataCollectionValue ReadResourceSet(ODataReader reader)
        {
            var items = new List<ODataResource>();
            while (reader.Read())
                if (reader.State == ODataReaderState.ResourceEnd)
                    items.Add((ODataResource)reader.Item);

            return new ODataCollectionValue() { Items = items };
        }
        public static async Task WriteCollectionAsync(IEdmModel model, ODataUri odataUri, Db.OeAsyncEnumerator asyncEnumerator, Stream responseStream)
        {
            var importSegment = (OperationImportSegment)odataUri.Path.LastSegment;
            IEdmOperationImport operationImport = importSegment.OperationImports.Single();
            Type returnType = model.GetClrType(operationImport.Operation.ReturnType.Definition);

            IODataRequestMessage requestMessage = new Infrastructure.OeInMemoryMessage(responseStream, null);
            using (ODataMessageWriter messageWriter = new ODataMessageWriter(requestMessage,
                new ODataMessageWriterSettings() { EnableMessageStreamDisposal = false, ODataUri = odataUri }, model))
            {
                IEdmTypeReference typeRef = OeEdmClrHelper.GetEdmTypeReference(model, returnType);
                ODataCollectionWriter writer = messageWriter.CreateODataCollectionWriter(typeRef);
                writer.WriteStart(new ODataCollectionStart());

                while (await asyncEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    Object value = asyncEnumerator.Current;
                    if (value != null && value.GetType().IsEnum)
                        value = value.ToString();

                    writer.WriteItem(value);
                }

                writer.WriteEnd();
            }
        }
    }
}
