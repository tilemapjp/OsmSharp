﻿// OsmSharp - OpenStreetMap tools & library.
//
// Copyright (C) 2013 Abelshausen Ben
//                    Alexander Sinitsyn
// 
// This file is part of OsmSharp.
// 
// OsmSharp is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// OsmSharp is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with OsmSharp. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Data.SqlClient;
using OsmSharp.Osm.Data;
using OsmSharp.Data.SQLServer.Osm.SchemaTools;
using OsmSharp.Math.Geo;
using OsmSharp.Osm;
using OsmSharp.Osm.Filters;

namespace OsmSharp.Data.SQLServer.Osm
{
    /// <summary>
    /// Allows a version of the OsmSharp simple schema to be queried in SQLServer.
    /// 
    /// http://www.osmsharp.com/wiki/simpleschema
    /// </summary>
    public class SQLServerDataSource : DataSourceReadOnlyBase, IDisposable
    {
        /// <summary>
        /// Holds the connection string.
        /// </summary>
        private string _connectionString;

        /// <summary>
        /// The id of this datasource.
        /// </summary>
        private Guid _id;

        /// <summary>
        /// Flag that indicates if the schema needs to be created if not present.
        /// </summary>
        private bool _createAndDetectSchema;

        /// <summary>
        /// Creates a new simple schema datasource.
        /// </summary>
        /// <param name="connectionString"></param>
        public SQLServerDataSource(string connectionString)
        {
            _connectionString = connectionString;
            _id = Guid.NewGuid();
            _createAndDetectSchema = false;
        }

        /// <summary>
        /// Creates a new simple schema datasource.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        /// <param name="createSchema">Creates all the needed tables if true.</param>
        public SQLServerDataSource(string connectionString, bool createSchema)
        {
            _connectionString = connectionString;
            _id = Guid.NewGuid();
            _createAndDetectSchema = createSchema;
        }

        /// <summary>
        /// Holds the connection to the SQLServer db.
        /// </summary>
        private SqlConnection _connection;

        /// <summary>
        /// Creates a new/gets the existing connection.
        /// </summary>
        /// <returns></returns>
        private SqlConnection CreateConnection()
        {
            if (_connection == null)
            {
                _connection = new SqlConnection(_connectionString);
                _connection.Open();

                if (_createAndDetectSchema)
                { // creates or detects the tables.
                    SQLServerSchemaTools.CreateAndDetect(_connection);
                }
            }
            return _connection;
        }

        #region IDataSourceReadOnly Members

        /// <summary>
        /// Not supported.
        /// </summary>
        public override GeoCoordinateBox BoundingBox
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Returns the name.
        /// </summary>
        public string Name
        {
            get
            {
                return "SQLServer Schema Source";
            }
        }

        /// <summary>
        /// Returns the id.
        /// </summary>
        public override Guid Id
        {
            get
            {
                return _id;
            }
        }

        /// <summary>
        /// Returns false; database sources have no bounding box.
        /// </summary>
        public override bool HasBoundinBox
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Returns all the nodes with the given ids.
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public override IList<Node> GetNodes(IList<long> ids)
        {
            IList<Node> return_list = new List<Node>();
            if (ids.Count > 0)
            {
                // initialize connection.
                SqlConnection con = this.CreateConnection();
                // STEP 1: query nodes table.
                //id	latitude	longitude	changeset_id	visible	timestamp	tile	version

                Dictionary<long, Node> nodes = new Dictionary<long, Node>();
                for (int idx_1000 = 0; idx_1000 <= ids.Count / 1000; idx_1000++)
                {
                    int start_idx = idx_1000 * 1000;
                    int stop_idx = System.Math.Min((idx_1000 + 1) * 1000, ids.Count);
                    string sql
                        = "SELECT * FROM node WHERE (id IN ({0})) ";
                    ;
                    string ids_string = this.ConstructIdList(ids,start_idx,stop_idx);
                    if (ids_string.Length > 0)
                    {
                        sql = string.Format(sql, ids_string);
                        
                        SqlCommand com = new SqlCommand(sql);
                        com.Connection = con;
                        SqlDataReader reader = com.ExecuteReader();
                        Node node = null;
                        List<long> node_ids = new List<long>();
                        while (reader.Read())
                        {
                            // load/parse data.
                            long returned_id = reader.GetInt64(0);
                            int latitude_int = reader.GetInt32(1);
                            int longitude_int = reader.GetInt32(2);
                            long changeset_id = reader.GetInt64(3);
                            bool visible = reader.GetBoolean(4);
                            DateTime timestamp = reader.GetDateTime(5);
                            long tile = reader.GetInt64(6);
                            long version = reader.GetInt32(7);

                            if (!nodes.ContainsKey(returned_id))
                            {
                                // create node.
                                node = new Node();
                                node.Id = returned_id;
                                node.Version = (ulong)version;
                                //node.UserId = user_id;
                                node.TimeStamp = timestamp;
                                node.ChangeSetId = changeset_id;
                                node.Latitude = ((double)latitude_int) / 10000000.0;
                                node.Longitude = ((double)longitude_int) / 10000000.0;

                                nodes.Add(node.Id.Value, node);
                                node_ids.Add(node.Id.Value);
                            }
                        }
                        reader.Close();
                    }
                }

                // STEP2: Load all node tags.
                this.LoadNodeTags(nodes);

                return_list = nodes.Values.ToList<Node>();
            }
            return return_list;
        }

        /// <summary>
        /// Returns all the relations with the given ids.
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public override IList<Relation> GetRelations(IList<long> ids)
        {
            // TODO: implement this
            return new List<Relation>();
        }

        /// <summary>
        /// Returns all relations for the given objects.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public override IList<Relation> GetRelationsFor(OsmGeoType type, long id)
        {
            // TODO: implement this
            return new List<Relation>();
        }

        /// <summary>
        /// Returns all ways with the given ids.
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public override IList<Way> GetWays(IList<long> ids)
        {
            if (ids.Count > 0)
            {
                SqlConnection con = this.CreateConnection();

                // STEP2: Load ways.
                Dictionary<long, Way> ways = new Dictionary<long, Way>();
                string sql;
                SqlCommand com;
                SqlDataReader reader;
                for (int idx_1000 = 0; idx_1000 <= ids.Count / 1000; idx_1000++)
                {
                    int start_idx = idx_1000 * 1000;
                    int stop_idx = System.Math.Min((idx_1000 + 1) * 1000, ids.Count);

                    sql = "SELECT * FROM way WHERE (id IN ({0})) ";
                    string ids_string = this.ConstructIdList(ids,start_idx,stop_idx);
                    if(ids_string.Length > 0)
                    {
                        sql = string.Format(sql, ids_string);
                        com = new SqlCommand(sql);
                        com.Connection = con;
                        reader = com.ExecuteReader();
                        Way way;
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            long changeset_id = reader.GetInt64(1);
                            DateTime timestamp = reader.GetDateTime(2);
                            bool visible = reader.GetBoolean(3);
                            long version = reader.GetInt32(4);

                            // create way.
                            way = new Way();
                            way.Id = id;
                            way.Version = (ulong)version;
                            //node.UserId = user_id;
                            way.TimeStamp = timestamp;
                            way.ChangeSetId = changeset_id;

                            ways.Add(way.Id.Value, way);
                        }
                        reader.Close();
                    }
                }

                //STEP3: Load all node-way relations
                List<long> missing_node_ids = new List<long>();
                for (int idx_1000 = 0; idx_1000 <= ids.Count / 1000; idx_1000++)
                {
                    int start_idx = idx_1000 * 1000;
                    int stop_idx = System.Math.Min((idx_1000 + 1) * 1000, ids.Count);

                    sql = "SELECT * FROM way_nodes WHERE (way_id IN ({0})) ORDER BY sequence_id";
                    string ids_string = this.ConstructIdList(ids, start_idx, stop_idx);
                    if (ids_string.Length > 0)
                    {
                        sql = string.Format(sql, ids_string);
                        com = new SqlCommand(sql);
                        com.Connection = con;
                        reader = com.ExecuteReader();
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            long node_id = reader.GetInt64(1);
                            long sequence_id = reader.GetInt32(2);

                            Way way;
                            if (ways.TryGetValue(id, out way))
                            {
                                if (way.Nodes == null)
                                {
                                    way.Nodes = new List<long>();
                                }
                                way.Nodes.Add(id);
                            }
                        }
                        reader.Close();
                    }
                }

                //STEP4: Load all tags.
                for (int idx_1000 = 0; idx_1000 <= ids.Count / 1000; idx_1000++)
                {
                    int start_idx = idx_1000 * 1000;
                    int stop_idx = System.Math.Min((idx_1000 + 1) * 1000, ids.Count);

                    sql = "SELECT * FROM way_tags WHERE (way_id IN ({0})) ";
                    string ids_string = this.ConstructIdList(ids, start_idx, stop_idx);
                    if (ids_string.Length > 0)
                    {
                        sql = string.Format(sql, ids_string);
                        com = new SqlCommand(sql);
                        com.Connection = con;
                        reader = com.ExecuteReader();
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            string key = reader.GetString(1);
                            object value_object = reader[2];
                            string value = string.Empty;
                            if (value_object != null && value_object != DBNull.Value)
                            {
                                value = (string)value_object;
                            }

                            Way way;
                            if (ways.TryGetValue(id, out way))
                            {
                                way.Tags.Add(key, value);
                            }
                        }
                        reader.Close();
                    }
                }

                return ways.Values.ToList<Way>();
            }
            return new List<Way>();
        }

        /// <summary>
        /// Returns all ways using the given node.
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public override IList<Way> GetWaysFor(long id)
        {
            List<long> ids = new List<long>();
            ids.Add(id);
            return this.GetWaysForNodes(ids);
        }

        /// <summary>
        /// Returns all ways using any of the given nodes.
        /// </summary>
        /// <param name="nodes"></param>
        /// <returns></returns>
        public IList<Way> GetWaysForNodes(List<long> ids)
        {
            if (ids.Count > 0)
            {
                SqlConnection con = this.CreateConnection();

                List<long> way_ids = new List<long>();
                for (int idx_100 = 0; idx_100 <= ids.Count / 100; idx_100++)
                {
                    // STEP1: Load ways that exist for the given nodes.
                    string sql = "SELECT * FROM way_nodes WHERE (node_id IN ({0})) ";
                    int start_idx = idx_100 * 100;
                    int stop_idx = System.Math.Min((idx_100 + 1) * 100, ids.Count);
                    string ids_string = this.ConstructIdList(ids, start_idx, stop_idx);
                    if (ids_string.Length > 0)
                    {
                        sql = string.Format(sql, ids_string);
                        SqlCommand com = new SqlCommand(sql);
                        com.Connection = con;
                        SqlDataReader reader = com.ExecuteReader();

                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            if (!way_ids.Contains(id))
                            {
                                way_ids.Add(id);
                            }
                        }
                        reader.Close();
                        com.Dispose();
                    }
                }

                return this.GetWays(way_ids);
            }
            return new List<Way>();
        }

        #region Tile Calculations

        uint xy2tile(uint x, uint y)
        {
            uint tile = 0;
            int i;

            for (i = 15; i >= 0; i--)
            {
                tile = (tile << 1) | ((x >> i) & 1);
                tile = (tile << 1) | ((y >> i) & 1);
            }

            return tile;
        }

        uint lon2x(double lon)
        {
            return (uint)System.Math.Floor(((lon + 180.0) * 65536.0 / 360.0));
        }

        uint lat2y(double lat)
        {
            return (uint)System.Math.Floor(((lat + 90.0) * 65536.0 / 180.0));
        }

        #endregion

        /// <summary>
        /// Returns all data within the given bounding box and filtered by the given filter.
        /// </summary>
        /// <param name="box"></param>
        /// <param name="filter"></param>
        /// <returns></returns>
        public override IList<OsmGeo> Get(GeoCoordinateBox box, Filter filter)
        {
            // initialize connection.
            SqlConnection con = this.CreateConnection();
            List<OsmGeo> base_list = new List<OsmGeo>();

            // calculate bounding box parameters to query db.
            long latitude_min = (long)(box.MinLat * 10000000.0);
            long longitude_min = (long)(box.MinLon * 10000000.0);
            long latitude_max = (long)(box.MaxLat * 10000000.0);
            long longitude_max = (long)(box.MaxLon * 10000000.0);

            // TODO: improve this to allow loading of bigger bb's.
            uint x_min = lon2x(box.MinLon);
            uint x_max = lon2x(box.MaxLon);
            uint y_min = lat2y(box.MinLat);
            uint y_max = lat2y(box.MaxLat);

            IList<long> boxes = new List<long>();

            for (uint x = x_min; x <= x_max; x++)
            {
                for (uint y = y_min; y <= y_max; y++)
                {
                    boxes.Add(this.xy2tile(x, y));
                }
            }

            // STEP 1: query nodes table.
            //id	latitude	longitude	changeset_id	visible	timestamp	tile	version
            string sql
                = "SELECT * FROM node WHERE (visible = 1) AND  (tile IN ({4})) AND (latitude BETWEEN {0} AND {1} AND longitude BETWEEN {2} AND {3})";
            sql = string.Format(sql,
                    latitude_min.ToString(),
                    latitude_max.ToString(),
                    longitude_min.ToString(),
                    longitude_max.ToString(),
                    this.ConstructIdList(boxes));

            // TODO: parameters.
            SqlCommand com = new SqlCommand(sql);
            com.Connection = con;
                SqlDataReader reader = com.ExecuteReader();
            Node node = null;
            Dictionary<long, Node> nodes = new Dictionary<long, Node>();
            List<long> nodeIds = new List<long>();
            while (reader.Read())
            {
                // load/parse data.
                long returned_id = reader.GetInt64(0);
                int latitude_int = reader.GetInt32(1);
                int longitude_int = reader.GetInt32(2);
                long changeset_id = reader.GetInt64(3);
                bool visible = reader.GetBoolean(4);
                DateTime timestamp = reader.GetDateTime(5);
                long tile = reader.GetInt64(6);
                long version = reader.GetInt32(7);

                // create node.
                node = new Node();
                node.Id = returned_id;
                node.Version = (ulong)version;
                //node.UserId = user_id;
                node.TimeStamp = timestamp;
                node.ChangeSetId = changeset_id;
                node.Longitude = ((double)longitude_int) / 10000000.0;
                node.Latitude = ((double)latitude_int) / 10000000.0;

                nodes.Add(node.Id.Value,node);
                nodeIds.Add(node.Id.Value);
            }
            reader.Close();

            // STEP2: Load all node tags.
            this.LoadNodeTags(nodes);            

            // STEP3: Load all ways for the given nodes.
            IList<Way> ways = this.GetWaysForNodes(nodeIds);

            // Add all objects to the base list.
            foreach (Node node_result in nodes.Values.ToList<Node>())
            {
                base_list.Add(node_result);
            }
            foreach (Way way in ways)
            {
                base_list.Add(way);
            }
            return base_list;
        }

        /// <summary>
        /// Constructs an id list for SQL.
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        private string ConstructIdList(IList<long> ids)
        {
            return this.ConstructIdList(ids, 0, ids.Count);
        }

        /// <summary>
        /// Constructs an id list for SQL for only the specified section of ids.
        /// </summary>
        /// <param name="ids"></param>
        /// <param name="start_idx"></param>
        /// <param name="end_idx"></param>
        /// <returns></returns>
        private string ConstructIdList(IList<long> ids,int start_idx,int end_idx)
        {
            string return_string = string.Empty;
            if (ids.Count > 0 && ids.Count > start_idx)
            {
                return_string = return_string + ids[start_idx].ToString();
                for (int i = start_idx + 1; i < end_idx; i++)
                {
                    return_string = return_string + "," + ids[i].ToString();
                }
            }
            return return_string;
        }

        /// <summary>
        /// Loads all tags for the given nodes.
        /// </summary>
        /// <param name="nodes"></param>
        private void LoadNodeTags(Dictionary<long,Node> nodes)
        {
            if (nodes.Count > 0)
            {
                for (int idx_1000 = 0; idx_1000 <= nodes.Count / 1000; idx_1000++)
                {
                    string sql = "SELECT * FROM node_tags WHERE (node_id IN ({0})) ";
                    int start_idx = idx_1000 * 1000;
                    int stop_idx = System.Math.Min((idx_1000 + 1) * 1000,nodes.Count);
                    string ids = this.ConstructIdList(nodes.Keys.ToList<long>(), start_idx,stop_idx);
                    if(ids.Length > 0)
                    {
                        sql = string.Format(sql, ids);
                        SqlConnection con = this.CreateConnection();
                        SqlCommand com = new SqlCommand(sql);
                        com.Connection = con;
                        SqlDataReader reader = com.ExecuteReader();
                        while (reader.Read())
                        {
                            long returned_id = reader.GetInt64(0);
                            string key = reader.GetString(1);
                            object val = reader.GetValue(2);
                            string value = string.Empty;
                            if (val is string)
                            {
                                value = val as string;
                            }

                            nodes[returned_id].Tags.Add(key, value);

                        }
                        reader.Close();
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Closes this datasource.
        /// </summary>
        public void Close()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection = null;
            }
        }

        #region IDisposable Members

        /// <summary>
        /// Diposes the resources used in this datasource.
        /// </summary>
        public void Dispose()
        {
            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }

        #endregion
    }
}