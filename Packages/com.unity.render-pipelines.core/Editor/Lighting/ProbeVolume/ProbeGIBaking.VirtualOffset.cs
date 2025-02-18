using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace UnityEngine.Rendering
{
    partial class ProbeGIBaking
    {
        struct VirtualOffsetBaking
        {
            static int k_MaxProbeCountPerBatch = 65535;

            static readonly int _Probes = Shader.PropertyToID("_Probes");
            static readonly int _Offsets = Shader.PropertyToID("_Offsets");

            // Duplicated in HLSL
            struct ProbeData
            {
                public Vector3 position;
                public float originBias;
                public float tMax;
                public float geometryBias;
                public int probeIndex;
                internal float _;
            };

            int batchPosIdx;
            Vector3[] positions;
            Dictionary<int, TouchupsPerCell> cellToVolumes;
            ProbeData[] probeData;
            Vector3[] batchResult;

            float scaleForSearchDist;
            float rayOriginBias;
            float geometryBias;

            // Output buffer
            public Vector3[] offsets;

            private IRayTracingAccelStruct m_AccelerationStructure;
            private GraphicsBuffer probeBuffer;
            private GraphicsBuffer offsetBuffer;
            private GraphicsBuffer scratchBuffer;

            public ulong currentStep => (ulong)batchPosIdx;
            public ulong stepCount => positions == null ? 0 : (ulong)positions.Length;

            public void Initialize(ProbeVolumeBakingSet bakingSet, Vector3[] probePositions)
            {
                var voSettings = bakingSet.settings.virtualOffsetSettings;
                scaleForSearchDist = voSettings.searchMultiplier;
                rayOriginBias = voSettings.rayOriginBias;
                geometryBias = voSettings.outOfGeoOffset;

                batchPosIdx = 0;
                positions = null;
                offsets = null;

                if (!voSettings.useVirtualOffset)
                    return;

                offsets = new Vector3[probePositions.Length];
                cellToVolumes = GetTouchupsPerCell(out bool hasAppliers);

                if (scaleForSearchDist == 0.0f)
                {
                    if (hasAppliers)
                        DoApplyVirtualOffsetsFromAdjustmentVolumes(probePositions, offsets, cellToVolumes);
                    return;
                }

                positions = probePositions;
                probeData = new ProbeData[k_MaxProbeCountPerBatch];
                batchResult = new Vector3[k_MaxProbeCountPerBatch];

                var computeBufferTarget = GraphicsBuffer.Target.CopyDestination | GraphicsBuffer.Target.CopySource
                    | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw;

                // Create acceletation structure
                m_AccelerationStructure = BuildAccelerationStructure(voSettings.collisionMask);
                var virtualOffsetShader = s_TracingContext.shaderVO;

                probeBuffer = new GraphicsBuffer(computeBufferTarget, k_MaxProbeCountPerBatch, Marshal.SizeOf<ProbeData>());
                offsetBuffer = new GraphicsBuffer(computeBufferTarget, k_MaxProbeCountPerBatch, Marshal.SizeOf<Vector3>());
                scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure, virtualOffsetShader,
                    (uint)k_MaxProbeCountPerBatch, 1, 1);

                var cmd = new CommandBuffer();
                m_AccelerationStructure.Build(cmd, scratchBuffer);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

            static IRayTracingAccelStruct BuildAccelerationStructure(int mask)
            {
                var accelStruct = s_TracingContext.CreateAccelerationStructure();
                var contributors = m_BakingBatch.contributors;

                foreach (var renderer in contributors.renderers)
                {
                    int layerMask = 1 << renderer.component.gameObject.layer;
                    if ((layerMask & mask) == 0)
                        continue;

                    var mesh = renderer.component.GetComponent<MeshFilter>().sharedMesh;
                    if (mesh == null)
                        continue;

                    int subMeshCount = mesh.subMeshCount;
                    for (int i = 0; i < subMeshCount; ++i)
                    {
                        accelStruct.AddInstance(new MeshInstanceDesc(mesh, i)
                        {
                            localToWorldMatrix = renderer.component.transform.localToWorldMatrix,
                            enableTriangleCulling = false
                        });
                    }
                }

                foreach (var terrain in contributors.terrains)
                {
                    int layerMask = 1 << terrain.component.gameObject.layer;
                    if ((layerMask & mask) == 0)
                        continue;

                    accelStruct.AddTerrain(new TerrainDesc(terrain.component)
                    {
                        localToWorldMatrix = terrain.component.transform.localToWorldMatrix,
                        enableTriangleCulling = false
                    });
                }

                return accelStruct;
            }

            public void RunVirtualOffsetStep()
            {
                if (batchPosIdx >= positions.Length)
                    return;

                var cmd = new CommandBuffer();
                var virtualOffsetShader = s_TracingContext.shaderVO;

                virtualOffsetShader.SetAccelerationStructure(cmd, "_AccelStruct", m_AccelerationStructure);
                virtualOffsetShader.SetBufferParam(cmd, _Probes, probeBuffer);
                virtualOffsetShader.SetBufferParam(cmd, _Offsets, offsetBuffer);

                // Run one batch of computations
                float cellSize = m_ProfileInfo.cellSizeInMeters;
                float minBrickSize = m_ProfileInfo.minBrickSize;
                {
                    // Prepare batch
                    int probeCountInBatch = 0;
                    do
                    {
                        int subdivLevel = m_BakingBatch.GetSubdivLevelAt(positions[batchPosIdx]);
                        var brickSize = ProbeReferenceVolume.CellSize(subdivLevel);
                        var searchDistance = (brickSize * minBrickSize) / ProbeBrickPool.kBrickCellCount;
                        var distanceSearch = scaleForSearchDist * searchDistance;

                        int cellIndex = PosToIndex(Vector3Int.FloorToInt(positions[batchPosIdx] / cellSize));
                        if (cellToVolumes.TryGetValue(cellIndex, out var volumes))
                        {
                            bool adjusted = false;
                            foreach (var (touchup, obb, center, offset) in volumes.appliers)
                            {
                                if (touchup.ContainsPoint(obb, center, positions[batchPosIdx]))
                                {
                                    positions[batchPosIdx] += offset;
                                    offsets[batchPosIdx] = offset;
                                    adjusted = true;
                                    break;
                                }
                            }

                            if (adjusted)
                                continue;

                            foreach (var (touchup, obb, center) in volumes.overriders)
                            {
                                if (touchup.ContainsPoint(obb, center, positions[batchPosIdx]))
                                {
                                    rayOriginBias = touchup.rayOriginBias;
                                    geometryBias = touchup.geometryBias;
                                    break;
                                }
                            }
                        }

                        probeData[probeCountInBatch++] = new ProbeData
                        {
                            position = positions[batchPosIdx],
                            originBias = rayOriginBias,
                            tMax = distanceSearch,
                            geometryBias = geometryBias,
                            probeIndex = batchPosIdx,
                        };
                    }
                    while (++batchPosIdx < positions.Length && probeCountInBatch < k_MaxProbeCountPerBatch);

                    // Execute job
                    cmd.SetBufferData(probeBuffer, probeData);
                    virtualOffsetShader.Dispatch(cmd, scratchBuffer, (uint)probeCountInBatch, 1, 1);

                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    offsetBuffer.GetData(batchResult);
                    for (int i = 0; i < probeCountInBatch; i++)
                        offsets[probeData[i].probeIndex] = batchResult[i];
                }

                cmd.Dispose();
            }

            public void Dispose()
            {
                if (positions == null)
                    return;

                m_AccelerationStructure.Dispose();
                probeBuffer.Dispose();
                offsetBuffer.Dispose();
                scratchBuffer?.Dispose();

                this = default;
            }
        }

        static internal void RecomputeVOForDebugOnly()
        {
            var prv = ProbeReferenceVolume.instance;
            if (prv.perSceneDataList.Count == 0)
                return;

            SetBakingContext(prv.perSceneDataList);

            if (!m_BakingSet.HasBeenBaked())
                return;

            globalBounds = prv.globalBounds;
            CellCountInDirections(out minCellPosition, out maxCellPosition, prv.MaxBrickSize());
            cellCount = maxCellPosition + Vector3Int.one - minCellPosition;

            var bakingBatch = new BakingBatch(cellCount);
            m_ProfileInfo = new ProbeVolumeProfileInfo();
            ModifyProfileFromLoadedData(m_BakingSet);

            List <Vector3> positionList = new();
            Dictionary<int, int> positionToIndex = new();
            foreach (var cell in ProbeReferenceVolume.instance.cells.Values)
            {
                var bakingCell = ConvertCellToBakingCell(cell.desc, cell.data);

                int numProbes = bakingCell.probePositions.Length;
                int uniqueIndex = positionToIndex.Count;
                var indices = new int[numProbes];

                // DeduplicateProbePositions
                for (int i = 0; i < numProbes; i++)
                {
                    var pos = bakingCell.probePositions[i];
                    int brickSubdiv = bakingCell.bricks[i / 64].subdivisionLevel;
                    int probeHash = bakingBatch.GetProbePositionHash(pos);

                    if (positionToIndex.TryGetValue(probeHash, out var index))
                    {
                        indices[i] = index;
                        int oldBrickLevel = bakingBatch.uniqueBrickSubdiv[probeHash];
                        if (brickSubdiv < oldBrickLevel)
                            bakingBatch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                    }
                    else
                    {
                        positionToIndex[probeHash] = uniqueIndex;
                        indices[i] = uniqueIndex;
                        bakingBatch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                        positionList.Add(pos);
                        uniqueIndex++;
                    }
                }

                bakingCell.probeIndices = indices;
                bakingBatch.cells.Add(bakingCell);

                // We need to force rebuild debug stuff.
                cell.debugProbes = null;
            }

            VirtualOffsetBaking job = new();
            job.Initialize(m_BakingSet, positionList.ToArray());

            while (job.currentStep < job.stepCount)
                job.RunVirtualOffsetStep();

            foreach (var cell in bakingBatch.cells)
            {
                int numProbes = cell.probePositions.Length;
                for (int i = 0; i < numProbes; ++i)
                {
                    int j = cell.probeIndices[i];
                    cell.offsetVectors[i] = job.offsets[j];
                }
            }

            job.Dispose();

            // Unload it all as we are gonna load back with newly written cells.
            foreach (var sceneData in prv.perSceneDataList)
                prv.AddPendingSceneRemoval(sceneData.sceneGUID);

            // Make sure unloading happens.
            prv.PerformPendingOperations();

            // Write back the assets.
            WriteBakingCells(bakingBatch.cells.ToArray());

            foreach (var data in prv.perSceneDataList)
                data.ResolveCellData();

            // We can now finally reload.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (var sceneData in prv.perSceneDataList)
            {
                prv.AddPendingSceneLoading(sceneData.sceneGUID, sceneData.bakingSet);
            }

            prv.PerformPendingOperations();
        }
    }

    partial class ProbeGIBaking
    {
        struct TouchupsPerCell
        {
            public List<(ProbeTouchupVolume touchup, ProbeReferenceVolume.Volume obb, Vector3 center, Vector3 offset)> appliers;
            public List<(ProbeTouchupVolume touchup, ProbeReferenceVolume.Volume obb, Vector3 center)> overriders;
        }

        static Dictionary<int, TouchupsPerCell> GetTouchupsPerCell(out bool hasAppliers)
        {
            float cellSize = m_ProfileInfo.cellSizeInMeters;
            hasAppliers = false;

            Dictionary<int, TouchupsPerCell> cellToVolumes = new();
            foreach (var touchup in Object.FindObjectsByType<ProbeTouchupVolume>(FindObjectsSortMode.InstanceID))
            {
                if (!touchup.isActiveAndEnabled || (touchup.mode != ProbeTouchupVolume.Mode.ApplyVirtualOffset && touchup.mode != ProbeTouchupVolume.Mode.OverrideVirtualOffsetSettings))
                    continue;

                hasAppliers |= touchup.mode == ProbeTouchupVolume.Mode.ApplyVirtualOffset;
                touchup.GetOBBandAABB(out var obb, out var aabb);

                Vector3Int min = Vector3Int.FloorToInt(aabb.min / cellSize);
                Vector3Int max = Vector3Int.FloorToInt(aabb.max / cellSize);

                for (int x = min.x; x <= max.x; x++)
                {
                    for (int y = min.y; y <= max.y; y++)
                    {
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var cell = PosToIndex(new Vector3Int(x, y, z));
                            if (!cellToVolumes.TryGetValue(cell, out var volumes))
                                cellToVolumes[cell] = volumes = new TouchupsPerCell() { appliers = new(), overriders = new() };

                            if (touchup.mode == ProbeTouchupVolume.Mode.ApplyVirtualOffset)
                                volumes.appliers.Add((touchup, obb, touchup.transform.position, touchup.GetVirtualOffset()));
                            else
                                volumes.overriders.Add((touchup, obb, touchup.transform.position));
                        }

                    }
                }
            }

            return cellToVolumes;
        }

        static Vector3[] DoApplyVirtualOffsetsFromAdjustmentVolumes(Vector3[] positions, Vector3[] offsets, Dictionary<int, TouchupsPerCell> cellToVolumes)
        {
            float cellSize = m_ProfileInfo.cellSizeInMeters;
            for (int i = 0; i < positions.Length; i++)
            {
                int cellIndex = PosToIndex(Vector3Int.FloorToInt(positions[i] / cellSize));
                if (cellToVolumes.TryGetValue(cellIndex, out var volumes))
                {
                    foreach (var (touchup, obb, center, offset) in volumes.appliers)
                    {
                        if (touchup.ContainsPoint(obb, center, positions[i]))
                        {
                            positions[i] += offset;
                            offsets[i] = offset;
                            break;
                        }
                    }
                }
            }
            return offsets;
        }

        enum InstanceFlags
        {
            DIRECT_RAY_VIS_MASK = 1,
            INDIRECT_RAY_VIS_MASK = 2,
            SHADOW_RAY_VIS_MASK = 4,
        }

        private static uint GetInstanceMask(ShadowCastingMode shadowMode)
        {
            uint instanceMask = 0u;

            if (shadowMode != ShadowCastingMode.Off)
                instanceMask |= (uint)InstanceFlags.SHADOW_RAY_VIS_MASK;

            if (shadowMode != ShadowCastingMode.ShadowsOnly)
            {
                instanceMask |= (uint)InstanceFlags.DIRECT_RAY_VIS_MASK;
                instanceMask |= (uint)InstanceFlags.INDIRECT_RAY_VIS_MASK;
            }

            return instanceMask;
        }

        static uint[] GetMaterialIndices(Renderer renderer)
        {
            int submeshCount = 1;
            var meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter)
                submeshCount = renderer.GetComponent<MeshFilter>().sharedMesh.subMeshCount;

            uint[] matIndices = new uint[submeshCount];
            for (int i = 0; i < matIndices.Length; ++i)
            {
                if (i < renderer.sharedMaterials.Length && renderer.sharedMaterials[i] != null)
                    matIndices[i] = (uint)renderer.sharedMaterials[i].GetInstanceID();
                else
                    matIndices[i] = 0;
            }

            return matIndices;
        }
    }
}
