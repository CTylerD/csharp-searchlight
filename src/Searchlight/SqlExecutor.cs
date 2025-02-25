﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Searchlight.Query;

namespace Searchlight
{
    public static class SqlExecutor
    {
        /// <summary>
        /// Convert this syntax tree to a SQL Query Builder for this data source
        /// </summary>
        /// <param name="query">The query to convert to SQL text</param>
        /// <param name="useMultiFetch">If true, will produce a multi result set query where the first result set is the total count of records for pagination purposes</param>
        public static SqlQuery ToSqlServerCommand(this SyntaxTree query, bool useMultiFetch)
        {
            var sql = new SqlQuery {Syntax = query};
            sql.WhereClause = RenderJoinedClauses(query.Filter, sql);
            sql.OrderByClause = RenderOrderByClause(query.OrderBy, sql);

            // Sanity tests
            if (sql.Parameters.Count > query.Source.MaximumParameters && query.Source.MaximumParameters > 0)
            {
                throw new TooManyParameters(query.Source.MaximumParameters, query.OriginalFilter);
            }
            var where = sql.WhereClause.Length > 0 ? $" WHERE {sql.WhereClause}" : "";
            var order = sql.OrderByClause.Length > 0 ? $" ORDER BY {sql.OrderByClause}" : "";
            var offset = "";
            if (query.PageNumber != null || query.PageSize != null)
            {
                var size  = query.PageSize ?? 50; // default page size
                var page = query.PageNumber ?? 0;
                offset = $" OFFSET {page * size} ROWS FETCH NEXT {size} ROWS ONLY";
            }

            // Apply all selected commands
            foreach (var cmd in query.Includes)
            {
                cmd.Apply(sql);
            }
            
            // If the user wants multi-fetch to retrieve row count
            if (useMultiFetch)
            {
                // If we're doing multi-fetch, we have to retrieve sorted/paginated records into a temp table before
                // joining with any child collections
                if (sql.ResultSetClauses.Count > 0)
                {
                    var commandClauses = sql.ResultSetClauses.Count > 0
                        ? String.Join("\n", sql.ResultSetClauses) + "\n"
                        : "";
                    sql.CommandText = $"SELECT COUNT(1) AS TotalRecords FROM {query.Source.TableName}{where};\n" +
                                      $"SELECT * INTO #temp FROM {query.Source.TableName}{where}{order}{offset};\n" +
                                      $"SELECT * FROM #temp{order};\n" +
                                      commandClauses +
                                      $"DROP TABLE #temp;\n";
                }
                else
                {
                    sql.CommandText = $"SELECT COUNT(1) AS TotalRecords FROM {query.Source.TableName}{where};\n" +
                                      $"SELECT * FROM {query.Source.TableName}{where}{order}{offset};\n";
                }
            }
            else
            {
                sql.CommandText = $"SELECT * FROM {query.Source.TableName}{where}{order}{offset}";
            }
            return sql;
        }

        public static string RenderOrderByClause(List<SortInfo> list, SqlQuery sql)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var sort = list[i];
                var dir = sort.Direction == SortDirection.Ascending ? "ASC" : "DESC";
                sb.Append($"{sort.Column.OriginalName} {dir}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Render a list of joined clauses using specified conjunctions
        /// </summary>
        /// <param name="clause"></param>
        /// <param name="sql"></param>
        /// <returns></returns>
        public static string RenderJoinedClauses(List<BaseClause> clause, SqlQuery sql)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < clause.Count; i++)
            {
                if (i > 0)
                {
                    switch (clause[i - 1].Conjunction)
                    {
                        case ConjunctionType.AND:
                            sb.Append(" AND ");
                            break;
                        case ConjunctionType.OR:
                            sb.Append(" OR ");
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                sb.Append(RenderClause(clause[i], sql));
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Convert a single clause object into SQL-formatted "WHERE" text
        /// </summary>
        /// <param name="clause"></param>
        /// <param name="sql"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public static string RenderClause(BaseClause clause, SqlQuery sql)
        {
            switch (clause)
            {
                case BetweenClause bc:
                    return $"{bc.Column.OriginalName} BETWEEN {sql.AddParameter(bc.LowerValue)} AND {sql.AddParameter(bc.UpperValue)}";
                case CompoundClause compoundClause:
                    return $"({RenderJoinedClauses(compoundClause.Children, sql)})";
                case CriteriaClause cc:
                    switch (cc.Operation)
                    {
                        case OperationType.Equals:
                            return $"{cc.Column.OriginalName} = {sql.AddParameter(cc.Value)}";
                        case OperationType.GreaterThan:
                            return $"{cc.Column.OriginalName} > {sql.AddParameter(cc.Value)}";
                        case OperationType.GreaterThanOrEqual:
                            return $"{cc.Column.OriginalName} >= {sql.AddParameter(cc.Value)}";
                        case OperationType.LessThan:
                            return $"{cc.Column.OriginalName} < {sql.AddParameter(cc.Value)}";
                        case OperationType.LessThanOrEqual:
                            return $"{cc.Column.OriginalName} <= {sql.AddParameter(cc.Value)}";
                        case OperationType.NotEqual:
                            return $"{cc.Column.OriginalName} <> {sql.AddParameter(cc.Value)}";
                        case OperationType.Contains:
                            if (cc.Value is not string)
                            {
                                throw new Exception("Value was not a string type");
                            }
                            return $"{cc.Column.OriginalName} LIKE {sql.AddParameter("%" + cc.Value + "%")}";
                        case OperationType.StartsWith:
                            if (cc.Value is not string)
                            {
                                throw new Exception("Value was not a string type");
                            }
                            return $"{cc.Column.OriginalName} LIKE {sql.AddParameter(cc.Value + "%")}";
                        case OperationType.EndsWith:
                            if (cc.Value is not string)
                            {
                                throw new Exception("Value was not a string type");
                            }
                            return $"{cc.Column.OriginalName} LIKE {sql.AddParameter("%" + cc.Value)}";
                        default:
                            throw new Exception("Incorrect clause type");
                    }
                case InClause ic:
                    var paramValues = from v in ic.Values select sql.AddParameter(v);
                    return $"{ic.Column.OriginalName} IN ({String.Join(", ", paramValues)})";
                case IsNullClause inc:
                    return $"{inc.Column.OriginalName} IS {(inc.Negated ? "NOT NULL" : "NULL")}";
                default:
                    throw new Exception("Unrecognized clause type.");
            }
        }
    }

    /*
    /// <summary>
    /// Database helper
    /// </summary>
    /// <typeparam name="KEY"></typeparam>
    /// <typeparam name="ENTITY"></typeparam>
    public class DbHelper<KEY, ENTITY>
    {
        private SafeQueryParser _parser;
        private Func<ENTITY, KEY> _primaryKeyFunc;
        private string _sqlTemplate;
        private string _defaultSortColumn;

        /// <summary>
        /// The default maximum size of any fetch is 1000 rows; you can override this by changing the value in the DbHelper.
        /// </summary>
        public const int MAX_RESULT_COUNT = 1000;

        #region Constructors
        /// <summary>
        /// Construct a generic DbHelper to solve basic use cases for an entity
        /// </summary>
        /// <param name="databaseType"></param>
        /// <param name="sqlTemplate"></param>
        /// <param name="defaultSortColumn"></param>
        /// <param name="tableAlias"></param>
        /// <param name="primaryKeyFunc"></param>
        public DbHelper(DatabaseType databaseType, 
            string sqlTemplate,
            string defaultSortColumn,
            string tableAlias, 
            Func<ENTITY, KEY> primaryKeyFunc = null)
        {
            _parser = new SafeQueryParser(new EntityColumnDefinitions(typeof(ENTITY)),
                new FullyQualifyColumnNames(tableAlias, databaseType), 
                databaseType);
            _sqlTemplate = sqlTemplate;
            _defaultSortColumn = defaultSortColumn;
            TableAlias = tableAlias;
            _primaryKeyFunc = primaryKeyFunc;
            MaxResults = MAX_RESULT_COUNT;
        }

        /// <summary>
        /// Construct a model-renaming DbHelper where the user can filter by model names rather than entity names
        /// </summary>
        /// <param name="databaseType"></param>
        /// <param name="modelType"></param>
        /// <param name="sqlTemplate"></param>
        /// <param name="defaultSortColumn"></param>
        /// <param name="tableAlias"></param>
        /// <param name="primaryKeyFunc"></param>
        /// <param name="dbFactory"></param>
        public DbHelper(DatabaseType databaseType,
            Type modelType,
            string sqlTemplate,
            string defaultSortColumn,
            string tableAlias,
            Func<ENTITY, KEY> primaryKeyFunc = null)
        {
            _parser = new SafeQueryParser(new ModelColumnDefinitions(modelType),
                new FullyQualifyColumnNames(tableAlias, databaseType),
                databaseType);
            _sqlTemplate = sqlTemplate;
            _defaultSortColumn = defaultSortColumn;
            TableAlias = tableAlias;
            _primaryKeyFunc = primaryKeyFunc;
            MaxResults = MAX_RESULT_COUNT;
        }

        /// <summary>
        /// Number of results to return at maximum
        /// </summary>
        public int MaxResults { get; set; }

        /// <summary>
        /// What is the official table alias for the SQL logic used in this DbHelper
        /// </summary>
        public string TableAlias { get; set; }
        #endregion

        #region Fetch implementation
        /// <summary>
        /// Fetch using the specified fetch request pattern
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="request"></param>
        /// <param name="commands"></param>
        /// <returns></returns>
        public FetchResult<ENTITY> Fetch(IDbConnectionFactory factory, FetchRequest request, List<OptionalCommand> commands = null)
        {
            var dp = new DynamicParameters();
            StringBuilder query = new StringBuilder(_sqlTemplate);
            if (commands == null) commands = new List<OptionalCommand>();

            // Generate the list of fields to select
            _parser.ParseSelectClause(request.Include, commands);

            // Give each command a chance to preview the request
            foreach (var c in commands) {
                c.Preview(request);
            }

            // Finalize the query
            var where = _parser.ParseWhereClause(request.Filter);
            var whereClause = where.ValidatedFilter;
            if (!string.IsNullOrEmpty(whereClause)) {
                whereClause = " WHERE " + whereClause;
            }

            // Combine where clause parameters into the ones we've already got
            dp.AddDynamicParams(where.SqlParameters);

            // Query all fields
            var fields = " * ";

            // Determine the sort filter, if any was supplied
            var orderByClause = _parser.ParseOrderByClause(request.SortBy, _defaultSortColumn).Expression;

            // If the user didn't fetch a specific number of results, return this many at maximum
            if (request.MaxResults <= 0) {
                request.MaxResults = MaxResults;
            }

            // Add sql for each optional fetch command that was requested
            bool multi_fetch = false;
            foreach (var command in commands) {
                if (command.IsIncluded) {
                    command.ApplySql(query, dp);
                    var fc = command as MultiFetchCommand<KEY, ENTITY>;
                    if (fc != null) multi_fetch = true;
                }
            }

            // Combine the string with all of the elements
            query = query
                .Replace("{selectClause}", fields)
                .Replace("{whereClause}", whereClause)
                .Replace("{orderByClause}", orderByClause)
                .Replace("{offset}", request.StartIndex.ToString())
                .Replace("{fetchNext}", request.MaxResults.ToString());

            // Enforce delay and logging rules
            var sql = query.ToString();
            DateTime startTime = DateTime.UtcNow;

            // Retrieve objects from the database
            using (var conn = factory.Create()) {
                conn.Open();

                // Multi fetch pattern
                var results = new FetchResult<ENTITY>();
                if (multi_fetch) {
                    using (var multi = conn.QueryMultiple(sql, dp, null, 0, System.Data.CommandType.Text)) {
                        var entities = multi.Read<ENTITY>().ToList();
                        results.value = entities;
                        Dictionary<KEY, ENTITY> dict = null;
                        if (_primaryKeyFunc != null) {
                            dict = results.value.ToDictionary(_primaryKeyFunc);
                        }

                        // Apply all fetch commands
                        foreach (var command in commands) {
                            var fc = command as MultiFetchCommand<KEY, ENTITY>;
                            if (fc != null) {
                                if (fc.IsIncluded) {
                                    fc.ExecuteCommand(entities, dict, multi);
                                }
                            }
                        }
                    }

                // Simple fetch pattern
                } else {
                    var entities = conn.Query<ENTITY>(sql, dp, null, true, null, System.Data.CommandType.Text).ToList();
                    results.value = entities;
                }

                // Construct the final fetch results object, and paginate
                if (results.value == null) results.value = new List<ENTITY>();
                results.count = results.value.Count;

                // Final results hook allows for filtering of the results
                foreach (var command in commands) {
                    if (command.IsIncluded) {
                        command.ApplyResults<ENTITY>(results);
                    }
                }

                // Apply database rules and notify watchers of a query that occurred
                var e = new DapperSqlEventArgs()
                {
                    Duration = DateTime.UtcNow - startTime,
                    RowCount = results.count ?? 0,
                    Sql = sql
                };
                GlobalDbHelperHook?.Invoke(this, e);

                // Here's your result
                return results;
            }
        }
        #endregion

        #region Hooks
        /// <summary>
        /// Hook this event to add functionality to all SQL statements everywhere
        /// </summary>
        public static event EventHandler GlobalDbHelperHook;
        #endregion
    }
    */
}
