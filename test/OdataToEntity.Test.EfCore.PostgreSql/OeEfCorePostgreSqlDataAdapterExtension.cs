﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.OData.Edm;
using OdataToEntity.ModelBuilder;
using System;

namespace OdataToEntity.EfCore
{
    public static class OeEfCorePostgreSqlDataAdapterExtension
    {
        public static EdmModel BuildEdmModelFromEfCorePgSqlModel(this Db.OeDataAdapter dataAdapter, String schema, params IEdmModel[] refModels)
        {
            var context = (DbContext)dataAdapter.CreateDataContext();
            try
            {
                var model = (Model)context.Model;
                model.Relational().DefaultSchema = schema;
                var modelBuilder = new OeEdmModelBuilder(dataAdapter, new OeEfCoreEdmModelMetadataProvider(model));
                return modelBuilder.BuildEdmModel(refModels);
            }
            finally
            {
                dataAdapter.CloseDataContext(context);
            }
        }
    }
}
