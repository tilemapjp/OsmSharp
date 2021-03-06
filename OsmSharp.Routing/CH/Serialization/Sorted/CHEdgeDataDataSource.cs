﻿// OsmSharp - OpenStreetMap (OSM) SDK
// Copyright (C) 2015 Abelshausen Ben
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

using OsmSharp.Collections.Cache;
using OsmSharp.Collections.Coordinates.Collections;
using OsmSharp.Collections.Tags.Index;
using OsmSharp.Math.Geo;
using OsmSharp.Math.Geo.Simple;
using OsmSharp.Osm.Tiles;
using OsmSharp.Routing.CH.PreProcessing;
using OsmSharp.Routing.Graph;
using OsmSharp.Routing.Graph.Router;
using System;
using System.Collections.Generic;
using System.IO;

namespace OsmSharp.Routing.CH.Serialization.Sorted
{
    /// <summary>
    /// A basic router datasource.
    /// </summary>
    internal class CHEdgeDataDataSource : IBasicRouterDataSource<CHEdgeData>
    {
        /// <summary>
        /// Holds the tags index.
        /// </summary>
        private ITagsCollectionIndexReadonly _tagsIndex;

        /// <summary>
        /// Holds the stream.
        /// </summary>
        private Stream _stream;

        /// <summary>
        /// Holds the serializer.
        /// </summary>
        private CHEdgeDataDataSourceSerializer _serializer;

        /// <summary>
        /// Holds the supported vehicles.
        /// </summary>
        private readonly HashSet<string> _vehicles;

        /// <summary>
        /// Creates a new CH edge data source.
        /// </summary>
        public CHEdgeDataDataSource(Stream stream, CHEdgeDataDataSourceSerializer serializer, IEnumerable<string> vehicles,
            int startOfRegions, CHVertexRegionIndex regionIndex, int zoom,
            int startOfBlocks, CHBlockIndex blockIndex, uint blockSize,
            int startOfShapes, CHBlockIndex shapeIndex,
            ITagsCollectionIndexReadonly tagsIndex)
        {
            _stream = stream;
            _serializer = serializer;
            _vehicles = new HashSet<string>(vehicles);

            this.InitializeRegions(startOfRegions, regionIndex, zoom);
            this.InitializeBlocks(startOfBlocks, blockIndex, blockSize);
            this.InitializeShapes(startOfShapes, shapeIndex);

            _blocks = new LRUCache<uint, CHBlock>(5000);
            _blockShapes = new LRUCache<uint, CHBlockCoordinates>(1000);
            _regions = new LRUCache<ulong, CHVertexRegion>(1000);
            _tagsIndex = tagsIndex;
        }

        /// <summary>
        /// Returns true if the given profile is supported.
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public bool SupportsProfile(Vehicle vehicle)
        {
            return _vehicles.Contains(vehicle.UniqueName);
        }

        /// <summary>
        /// Returns true if the given profile is supported.
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public void AddSupportedProfile(Vehicle vehicle)
        {
            throw new InvalidOperationException("Cannot add extra vehicle profiles to a read-only source.");
        }

        /// <summary>
        /// Returns all edges inside the given boundingbox.
        /// </summary>
        /// <param name="box"></param>
        /// <returns></returns>
        public KeyValuePair<uint, KeyValuePair<uint, CHEdgeData>>[] GetEdges(
            GeoCoordinateBox box)
        {
            var vertices = this.LoadVerticesIn(box);
            var arcs = new List<KeyValuePair<uint, KeyValuePair<uint, CHEdgeData>>>(vertices.Count * 3);
            foreach (uint vertexId in vertices)
            {
                var vertexArcs = this.GetEdges(vertexId);
                foreach (var arc in vertexArcs)
                {
                    if (vertexId < arc.Neighbour ||
                        !vertices.Contains(arc.Neighbour))
                    {
                        arcs.Add(new KeyValuePair<uint, KeyValuePair<uint, CHEdgeData>>(
                            vertexId, new KeyValuePair<uint, CHEdgeData>(arc.Neighbour, arc.EdgeData)));
                    }
                }
            }
            return arcs.ToArray();
        }

        /// <summary>
        /// Returns the tags index.
        /// </summary>
        public ITagsCollectionIndexReadonly TagsIndex
        {
            get { return _tagsIndex; }
        }

        /// <summary>
        /// Returns the location of the vertex with the given id.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="latitude"></param>
        /// <param name="longitude"></param>
        /// <returns></returns>
        public bool GetVertex(uint id, out float latitude, out float longitude)
        {
            return this.LoadVertex(id, out latitude, out longitude);
        }

        /// <summary>
        /// Returns all vertices in this router data source.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<uint> GetVertices()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Returns an enumerator for edges for the given vertex.
        /// </summary>
        /// <param name="vertexId"></param>
        /// <returns></returns>
        public IEdgeEnumerator<CHEdgeData> GetEdges(uint vertexId)
        {
            return new EdgeEnumerator(this.GetEdgePairs(vertexId));
        }

        /// <summary>
        /// Returns true if the given vertex has the given neighbour.
        /// </summary>
        /// <param name="vertexId"></param>
        /// <param name="neighbour"></param>
        /// <returns></returns>
        public bool ContainsEdge(uint vertexId, uint neighbour)
        {
            CHEdgeData data;
            return this.GetEdge(vertexId, neighbour, out data);
        }

        /// <summary>
        /// Returns true if the given vertex has the given neighbour.
        /// </summary>
        /// <param name="vertex1"></param>
        /// <param name="vertex2"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool GetEdge(uint vertex1, uint vertex2, out CHEdgeData data)
        {
            return this.LoadArc(vertex1, vertex2, out data);
        }

        /// <summary>
        /// Returns true if the given vertex has the given neighbour.
        /// </summary>
        /// <param name="vertex1"></param>
        /// <param name="vertex2"></param>
        /// <param name="shape"></param>
        /// <returns></returns>
        public bool GetEdgeShape(uint vertex1, uint vertex2, out ICoordinateCollection shape)
        {
            return this.LoadArcShape(vertex1, vertex2, out shape);
        }

        /// <summary>
        /// Returns the vertex count.
        /// </summary>
        public uint VertexCount
        {
            get { throw new NotSupportedException(); }
        }

        /// <summary>
        /// Represents a part of a stream.
        /// </summary>
        private class StreamPart
        {
            /// <summary>
            /// Gets/sets the offset.
            /// </summary>
            public long Offset { get; set; }

            /// <summary>
            /// Gets/sets the length.
            /// </summary>
            public int Length { get; set; }
        }

        /// <summary>
        /// Returns all arcs for the given vertex.
        /// </summary>
        /// <param name="vertexId"></param>
        /// <returns></returns>
        private KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[] GetEdgePairs(uint vertexId)
        {
            return this.LoadArcs(vertexId);
        }

        #region Regions

        /// <summary>
        /// The region zoom size.
        /// </summary>
        private int _zoom;

        /// <summary>
        /// Holds the regions.
        /// </summary>
        private LRUCache<ulong, CHVertexRegion> _regions;

        /// <summary>
        /// Holds the region stream parts.
        /// </summary>
        private Dictionary<ulong, StreamPart> _regionStreamParts;

        /// <summary>
        /// Initializes all region stuff.
        /// </summary>
        /// <param name="startOfRegions"></param>
        /// <param name="regionIndex"></param>
        /// <param name="zoom"></param>
        private void InitializeRegions(int startOfRegions, CHVertexRegionIndex regionIndex, int zoom)
        {
            _zoom = zoom;
            _regionStreamParts = new Dictionary<ulong, StreamPart>();

            for (int idx = 0; idx < regionIndex.LocationIndex.Length; idx++)
            {
                StreamPart streamPart = new StreamPart();
                if (idx == 0)
                { // start is at startOfRegions.
                    streamPart.Offset = startOfRegions;
                    streamPart.Length = regionIndex.LocationIndex[0];
                }
                else
                { // start is at startOfRegions + location end of previous block.
                    streamPart.Offset = startOfRegions + regionIndex.LocationIndex[idx - 1];
                    streamPart.Length = regionIndex.LocationIndex[idx] - regionIndex.LocationIndex[idx - 1];
                }
                _regionStreamParts.Add(regionIndex.RegionIds[idx],
                    streamPart);
            }
        }

        /// <summary>
        /// Loads all vertices inside the given boundingbox.
        /// </summary>
        /// <param name="box"></param>
        /// <returns></returns>
        private HashSet<uint> LoadVerticesIn(GeoCoordinateBox box)
        {
            var vertices = new HashSet<uint>();
            var range = TileRange.CreateAroundBoundingBox(box, _zoom);
            foreach (Tile tile in range)
            {
                CHVertexRegion region;
                if (!_regions.TryGet(tile.Id, out region))
                {
                    region = this.DeserializeRegion(tile.Id);
                    if (region != null)
                    {
                        _regions.Add(tile.Id, region);
                    }
                }
                if (region != null)
                {
                    for (int idx = 0; idx < region.Vertices.Length; idx++)
                    {
                        vertices.Add(region.Vertices[idx]);
                    }
                }
            }
            return vertices;
        }

        /// <summary>
        /// Deserializes a region with the given id.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private CHVertexRegion DeserializeRegion(ulong id)
        {
            StreamPart part;
            if (_regionStreamParts.TryGetValue(id, out part))
            {
                return _serializer.DeserializeRegion(_stream, part.Offset, part.Length, false);
            }
            return null;
        }

        #endregion

        #region Blocks

        /// <summary>
        /// Holds the blocksize.
        /// </summary>
        private uint _blockSize;

        /// <summary>
        /// Holds the cached blocks.
        /// </summary>
        private LRUCache<uint, CHBlock> _blocks;

        /// <summary>
        /// Holds the cached block shapes.
        /// </summary>
        private LRUCache<uint, CHBlockCoordinates> _blockShapes;

        /// <summary>
        /// Holds the start-position of the blocks.
        /// </summary>
        private int _startOfBlocks;

        /// <summary>
        /// Holds the blocks index.
        /// </summary>
        private CHBlockIndex _blocksIndex;

        /// <summary>
        /// Initializes the blocks stuff.
        /// </summary>
        /// <param name="startOfBlocks"></param>
        /// <param name="blocksIndex"></param>
        /// <param name="blockSize"></param>
        private void InitializeBlocks(int startOfBlocks, CHBlockIndex blocksIndex, uint blockSize)
        {
            _startOfBlocks = startOfBlocks;
            _blocksIndex = blocksIndex;
            _blockSize = blockSize;
        }

        /// <summary>
        /// Holds the start-position of the shapes.
        /// </summary>
        private int _startOfShapes;

        /// <summary>
        /// Holds the shapes index.
        /// </summary>
        private CHBlockIndex _shapesIndex;

        /// <summary>
        /// Holds the shapes index.
        /// </summary>
        private uint _shapesSize;

        /// <summary>
        /// Initializes the shapes stuff.
        /// </summary>
        /// <param name="startOfShapes"></param>
        /// <param name="shapesIndex"></param>
        private void InitializeShapes(int startOfShapes, CHBlockIndex shapesIndex)
        {
            _startOfShapes = startOfShapes;
            _shapesIndex = shapesIndex;
        }

        /// <summary>
        /// Loads a vertex and returns true if found.
        /// </summary>
        /// <param name="vertexId"></param>
        /// <param name="latitude"></param>
        /// <param name="longitude"></param>
        /// <returns></returns>
        private bool LoadVertex(uint vertexId, out float latitude, out float longitude)
        {
            uint blockId = CHBlock.CalculateId(vertexId, _blockSize);
            CHBlock block;
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    longitude = 0;
                    latitude = 0;
                    return false;
                }
                _blocks.Add(blockId, block);
            }
            uint blockIdx = vertexId - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                latitude = block.Vertices[blockIdx].Latitude;
                longitude = block.Vertices[blockIdx].Longitude;
                return true;
            }
            // oops even now the block is not found!
            longitude = 0;
            latitude = 0;
            return false;
        }

        /// <summary>
        /// Loads the edge between the given vertices.
        /// </summary>
        /// <param name="vertex1"></param>
        /// <param name="vertex2"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private bool LoadArc(uint vertex1, uint vertex2, out CHEdgeData data)
        {
            uint blockId = CHBlock.CalculateId(vertex1, _blockSize);
            CHBlock block;
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    data = new CHEdgeData();
                    return false;
                }
                _blocks.Add(blockId, block);
            }
            uint blockIdx = vertex1 - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                for (int arcIdx = block.Vertices[blockIdx].ArcIndex;
                    arcIdx < block.Vertices[blockIdx].ArcIndex + block.Vertices[blockIdx].ArcCount; arcIdx++)
                { // loop over all arcs.
                    var chArc = block.Arcs[arcIdx];
                    if (chArc.TargetId == vertex2)
                    {
                        data = new CHEdgeData();
                        data.BackwardContractedId = chArc.BackwardContractedId;
                        data.BackwardWeight = chArc.BackwardWeight;
                        data.ForwardContractedId = chArc.ForwardContractedId;
                        data.ForwardWeight = chArc.ForwardWeight;
                        data.ContractedDirectionValue = chArc.ContractedDirectionValue;
                        data.TagsValue = chArc.TagsValue;
                        return true;
                    }
                }
            }
            blockId = CHBlock.CalculateId(vertex2, _blockSize);
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    data = new CHEdgeData();
                    return false;
                }
                _blocks.Add(blockId, block);
            }
            blockIdx = vertex2 - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                for (int arcIdx = block.Vertices[blockIdx].ArcIndex;
                    arcIdx < block.Vertices[blockIdx].ArcIndex + block.Vertices[blockIdx].ArcCount; arcIdx++)
                { // loop over all arcs.
                    var chArc = block.Arcs[arcIdx];
                    if (chArc.TargetId == vertex1)
                    {
                        data = new CHEdgeData();
                        data.BackwardContractedId = chArc.BackwardContractedId;
                        data.BackwardWeight = chArc.BackwardWeight;
                        data.ForwardContractedId = chArc.ForwardContractedId;
                        data.ForwardWeight = chArc.ForwardWeight;
                        data.ContractedDirectionValue = chArc.ContractedDirectionValue;
                        data.TagsValue = chArc.TagsValue;
                        return true;
                    }
                }
            }
            data = new CHEdgeData();
            return false;
        }

        /// <summary>
        /// Loads the edge between the given vertices.
        /// </summary>
        /// <param name="vertex1"></param>
        /// <param name="vertex2"></param>
        /// <param name="shape"></param>
        /// <returns></returns>
        private bool LoadArcShape(uint vertex1, uint vertex2, out ICoordinateCollection shape)
        {
            uint blockId = CHBlock.CalculateId(vertex1, _blockSize);
            CHBlockCoordinates blockCoordinates;
            if (!_blockShapes.TryGet(blockId, out blockCoordinates))
            { // damn block not cached!
                blockCoordinates = this.DeserializeShape(blockId);
                if (blockCoordinates == null)
                { // oops even now the block is not found!
                    shape = null;
                    return false;
                }
                _blockShapes.Add(blockId, blockCoordinates);
            }
            CHBlock block;
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    shape = null;
                    return false;
                }
                _blocks.Add(blockId, block);
            }
            uint blockIdx = vertex1 - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                for (int arcIdx = block.Vertices[blockIdx].ArcIndex;
                    arcIdx < block.Vertices[blockIdx].ArcIndex + block.Vertices[blockIdx].ArcCount; arcIdx++)
                { // loop over all arcs.
                    var chArc = block.Arcs[arcIdx];
                    if (chArc.TargetId == vertex2)
                    {
                        var arcCoordinates = blockCoordinates.Arcs[arcIdx];
                        shape = null;
                        if (arcCoordinates.Coordinates != null)
                        {
                            shape = new CoordinateArrayCollection<GeoCoordinateSimple>(arcCoordinates.Coordinates);
                        }
                        return true;
                    }
                }
            }
            blockId = CHBlock.CalculateId(vertex2, _blockSize);
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    shape = null;
                    return false;
                }
                _blocks.Add(blockId, block);
            }
            if (!_blockShapes.TryGet(blockId, out blockCoordinates))
            { // damn block not cached!
                blockCoordinates = this.DeserializeShape(blockId);
                if (blockCoordinates == null)
                { // oops even now the block is not found!
                    shape = null;
                    return false;
                }
                _blockShapes.Add(blockId, blockCoordinates);
            }
            blockIdx = vertex2 - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                for (int arcIdx = block.Vertices[blockIdx].ArcIndex;
                    arcIdx < block.Vertices[blockIdx].ArcIndex + block.Vertices[blockIdx].ArcCount; arcIdx++)
                { // loop over all arcs.
                    var chArc = block.Arcs[arcIdx];
                    if (chArc.TargetId == vertex1)
                    {
                        var arcCoordinates = blockCoordinates.Arcs[arcIdx];
                        shape = null;
                        if (arcCoordinates.Coordinates != null)
                        {
                            shape = new CoordinateArrayCollection<GeoCoordinateSimple>(arcCoordinates.Coordinates);
                        }
                        return true;
                    }
                }
            }
            shape = null;
            return false;
        }

        /// <summary>
        /// Loads all arcs associated with the given vertex.
        /// </summary>
        /// <param name="vertexId"></param>
        /// <returns></returns>
        private KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[] LoadArcs(uint vertexId)
        {
            uint blockId = CHBlock.CalculateId(vertexId, _blockSize);
            CHBlock block;
            if (!_blocks.TryGet(blockId, out block))
            { // damn block not cached!
                block = this.DeserializeBlock(blockId);
                if (block == null)
                { // oops even now the block is not found!
                    return new KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[0];
                }
                _blocks.Add(blockId, block);
            }
            CHBlockCoordinates blockCoordinates;
            if (!_blockShapes.TryGet(blockId, out blockCoordinates))
            { // damn block not cached!
                blockCoordinates = this.DeserializeShape(blockId);
                if (blockCoordinates == null)
                { // oops even now the block is not found!
                    return new KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[0];
                }
                _blockShapes.Add(blockId, blockCoordinates);
            }
            uint blockIdx = vertexId - blockId;
            if (block.Vertices != null &&
                blockIdx < block.Vertices.Length)
            { // block is found and the vertex is there!
                var arcs = new KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[
                    block.Vertices[blockIdx].ArcCount];
                for (int arcIdx = block.Vertices[blockIdx].ArcIndex;
                    arcIdx < block.Vertices[blockIdx].ArcIndex + block.Vertices[blockIdx].ArcCount; arcIdx++)
                { // loop over all arcs.
                    var chArc = block.Arcs[arcIdx];
                    var edgeData = new CHEdgeData();
                    edgeData.BackwardContractedId = chArc.BackwardContractedId;
                    edgeData.BackwardWeight = chArc.BackwardWeight;
                    edgeData.ForwardContractedId = chArc.ForwardContractedId;
                    edgeData.ForwardWeight = chArc.ForwardWeight;
                    edgeData.ContractedDirectionValue = chArc.ContractedDirectionValue;
                    edgeData.TagsValue = chArc.TagsValue;

                    var chArcCoordinates = blockCoordinates.Arcs[arcIdx];
                    ICoordinateCollection coordinateCollection = null;
                    if (chArcCoordinates.Coordinates != null)
                    {
                        coordinateCollection = new CoordinateArrayCollection<GeoCoordinateSimple>(chArcCoordinates.Coordinates);
                    }
                    arcs[arcIdx - block.Vertices[blockIdx].ArcIndex] = new KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>(
                        chArc.TargetId, new KeyValuePair<CHEdgeData, ICoordinateCollection>(edgeData, coordinateCollection));
                }
                return arcs;
            }
            // oops even now the block is not found!
            return new KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[0];
        }

        /// <summary>
        /// Deserialize the block with the given id.
        /// </summary>
        /// <param name="blockId"></param>
        /// <returns></returns>
        private CHBlock DeserializeBlock(uint blockId)
        {
            int blockOffset;
            int blockSize;
            uint blockIdx = blockId / _blockSize;
            if (blockIdx == 0)
            { // the block idx zero.
                blockOffset = _startOfBlocks;
                blockSize = _blocksIndex.BlockLocationIndex[blockIdx];
            }
            else
            { // need to calculate offset and size.
                blockOffset = _startOfBlocks + _blocksIndex.BlockLocationIndex[blockIdx - 1];
                blockSize = _blocksIndex.BlockLocationIndex[blockIdx] - _blocksIndex.BlockLocationIndex[blockIdx - 1];
            }

            return _serializer.DeserializeBlock(_stream, blockOffset, blockSize, true);
        }

        /// <summary>
        /// Deserialize the shape with the given id.
        /// </summary>
        /// <param name="blockId"></param>
        /// <returns></returns>
        private CHBlockCoordinates DeserializeShape(uint blockId)
        {
            int blockOffset;
            int blockSize;
            uint blockIdx = blockId / _blockSize;
            if (blockIdx == 0)
            { // the block idx zero.
                blockOffset = _startOfShapes;
                blockSize = _shapesIndex.BlockLocationIndex[blockIdx];
            }
            else
            { // need to calculate offset and size.
                blockOffset = _startOfShapes + _shapesIndex.BlockLocationIndex[blockIdx - 1];
                blockSize = _shapesIndex.BlockLocationIndex[blockIdx] - _shapesIndex.BlockLocationIndex[blockIdx - 1];
            }

            return _serializer.DeserializeBlockShape(_stream, blockOffset, blockSize, true);
        }

        #endregion

        /// <summary>
        /// An edge enumerator.
        /// </summary>
        private class EdgeEnumerator : IEdgeEnumerator<CHEdgeData>
        {
            /// <summary>
            /// Holds the edges.
            /// </summary>
            private KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[] _edges;

            /// <summary>
            /// Holds the current position.
            /// </summary>
            private int _current = -1;

            /// <summary>
            /// Creates a new enumerator.
            /// </summary>
            /// <param name="edges"></param>
            public EdgeEnumerator(KeyValuePair<uint, KeyValuePair<CHEdgeData, ICoordinateCollection>>[] edges)
            {
                _edges = edges;
            }

            /// <summary>
            /// Moves to the next coordinate.
            /// </summary>
            /// <returns></returns>
            public bool MoveNext()
            {
                _current++;
                return _edges.Length > _current;
            }

            /// <summary>
            /// Returns the current neighbour.
            /// </summary>
            public uint Neighbour
            {
                get { return _edges[_current].Key; }
            }

            /// <summary>
            /// Returns the current edge data.
            /// </summary>
            public CHEdgeData EdgeData
            {
                get { return _edges[_current].Value.Key; }
            }

            /// <summary>
            /// Returns true if the edge data is inverted by default.
            /// </summary>
            public bool isInverted
            {
                get { return false; }
            }

            /// <summary>
            /// Returns the inverted edge data.
            /// </summary>
            public CHEdgeData InvertedEdgeData
            {
                get { return (CHEdgeData)this.EdgeData.Reverse(); }
            }

            /// <summary>
            /// Returns the current intermediates.
            /// </summary>
            public ICoordinateCollection Intermediates
            {
                get { return _edges[_current].Value.Value; }
            }

            /// <summary>
            /// Returns the count.
            /// </summary>
            /// <returns></returns>
            public int Count()
            {
                int count = 0;
                while (this.MoveNext())
                {
                    count++;
                }
                return count;
            }

            /// <summary>
            /// Resets this enumerator.
            /// </summary>
            public void Reset()
            {
                _current = -1;
            }

            public IEnumerator<Edge<CHEdgeData>> GetEnumerator()
            {
                this.Reset();
                return this;
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                this.Reset();
                return this;
            }

            public Edge<CHEdgeData> Current
            {
                get { return new Edge<CHEdgeData>(this); }
            }

            object System.Collections.IEnumerator.Current
            {
                get { return this; }
            }

            public void Dispose()
            {

            }
        }


        public void AddRestriction(uint[] route)
        {
            throw new NotImplementedException();
        }

        public void AddRestriction(string vehicleType, uint[] route)
        {
            throw new NotImplementedException();
        }

        public bool TryGetRestrictionAsStart(Vehicle vehicle, uint vertex, out List<uint[]> routes)
        {
            throw new NotImplementedException();
        }

        public bool TryGetRestrictionAsEnd(Vehicle vehicle, uint vertex, out List<uint[]> routes)
        {
            throw new NotImplementedException();
        }
    }
}