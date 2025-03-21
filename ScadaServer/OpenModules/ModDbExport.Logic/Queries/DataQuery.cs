﻿// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Scada.Data.Entities;
using Scada.Data.Models;
using Scada.Data.Tables;
using Scada.Dbms;
using Scada.MultiDb;
using Scada.Server.Modules.ModDbExport.Config;
using System.Data.Common;

namespace Scada.Server.Modules.ModDbExport.Logic.Queries
{
    /// <summary>
    /// Represents a query for exporting current and historical data.
    /// <para>Представляет запрос для экспорта текущих и исторических данных.</para>
    /// </summary>
    internal class DataQuery : Query
    {
        /// <summary>
        /// Represents query parameters.
        /// </summary>
        public class QueryParameters
        {
            public DbParameter Timestamp { get; init; }
            public DbParameter CnlNum { get; init; }
            public DbParameter ObjNum { get; init; }
            public DbParameter DeviceNum { get; init; }
            public DbParameter Val { get; init; }
            public DbParameter Stat { get; init; }
        }


        /// <summary>
        /// Initializes a new instance of the class.
        /// </summary>
        public DataQuery(int queryID, QueryOptions queryOptions, DataSource dataSource)
            : base(queryID, queryOptions, dataSource)
        {
            if (dataSource.ConnectionOptions.KnownDBMS == KnownDBMS.Oracle)
            {
                // OracleCommand requires parameters to be strictly SQL-compliant
                // https://docs.oracle.com/en/database/oracle/oracle-database/21/odpnt/CommandBindByName.html

                Parameters = queryOptions.SingleQuery
                    ? new QueryParameters
                    {
                        Timestamp = DataSource.SetParam(Command, "timestamp", DateTime.MinValue),
                    }
                    : new QueryParameters
                    {
                        Timestamp = DataSource.SetParam(Command, "timestamp", DateTime.MinValue),
                        CnlNum = DataSource.SetParam(Command, "cnlNum", 0),
                        Val = DataSource.SetParam(Command, "val", 0.0),
                        Stat = DataSource.SetParam(Command, "stat", 0),
                    };

                CnlPropsRequired = false;
            }
            else
            {
                Parameters = new QueryParameters
                {
                    Timestamp = DataSource.SetParam(Command, "timestamp", DateTime.MinValue),
                    CnlNum = DataSource.SetParam(Command, "cnlNum", 0),
                    ObjNum = DataSource.SetParam(Command, "objNum", 0),
                    DeviceNum = DataSource.SetParam(Command, "deviceNum", 0),
                    Val = DataSource.SetParam(Command, "val", 0.0),
                    Stat = DataSource.SetParam(Command, "stat", 0),
                };

                CnlPropsRequired = !string.IsNullOrEmpty(queryOptions.Sql) &&
                    (queryOptions.Sql.Contains("@objNum", StringComparison.OrdinalIgnoreCase) ||
                    queryOptions.Sql.Contains("@deviceNum", StringComparison.OrdinalIgnoreCase));
            }

            CombinedFilter = new CombinedFilter();
        }


        /// <summary>
        /// Gets the query parameters.
        /// </summary>
        public QueryParameters Parameters { get; }

        /// <summary>
        /// Gets a value indicating whether channel properties are required to define query parameters.
        /// </summary>
        public bool CnlPropsRequired { get; }

        /// <summary>
        /// Gets the combined query filter.
        /// </summary>
        public CombinedFilter CombinedFilter { get; }


        /// <summary>
        /// Sets the command parameters that represents value and status of the specified channel.
        /// </summary>
        public void SetCnlDataParams(int cnlNum, CnlData cnlData)
        {
            DataSource.SetParam(Command, "val" + cnlNum, cnlData.Val);
            DataSource.SetParam(Command, "stat" + cnlNum, cnlData.Stat);
        }

        /// <summary>
        /// Initializes the combined query filter.
        /// </summary>
        public void InitCombinedFilter(ConfigDatabase configDatabase)
        {
            CombinedFilter.Init(Options, configDatabase);
        }
    }
}
