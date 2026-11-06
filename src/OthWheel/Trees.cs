using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthWheel;

// The trees around the car, for the smart camera. Trees only have a trunk collider (for crashing into), so
// physics rays pass through their leaves; their canopies come from the terrain's tree list instead
// (TerrainData.treeInstances + treePrototypes, the data GPU Instancer draws them from). Each canopy is a
// vertical cylinder from 30% of the tree's height to its top, its radius from the tree model's size.
// The list of trees within 45 m is rebuilt every 20 m driven. Anything unreadable switches it off with one
// log line (the camera keeps working).
internal static class Trees
{
    struct Canopy { public Vector3 Base; public float R, Y0, Y1; }

    static readonly List<Canopy> _near = new();
    static readonly Dictionary<IntPtr, (float r, float h, string name)> _protoSize = new();
    static Vector3 _at = new(1e9f, 0f, 0f);
    static bool _logged, _broken;
    const float Radius = 45f;

    public static int Near => _near.Count;

    public static void Refresh(Vector3 pos)
    {
        if (_broken || (pos - _at).sqrMagnitude < 20f * 20f) return;
        _at = pos;
        try { Rebuild(pos); }
        catch (Exception e) { _broken = true; _near.Clear(); Plugin.ErrorOnce("trees (switched off)", e); }
    }

    static void Rebuild(Vector3 pos)
    {
        _near.Clear();
        int total = 0, terrainsWithTrees = 0;
        var terrains = Terrain.activeTerrains;
        if (terrains == null) return;
        var protoNames = new List<string>();
        foreach (var t in terrains)
        {
            if (t == null || t.terrainData == null) continue;
            var td = t.terrainData;
            var o = t.transform.position;
            var size = td.size;
            var inst = td.treeInstances;
            int n = inst?.Length ?? 0;
            total += n;
            if (n > 0) terrainsWithTrees++;
            // Skip terrains nowhere near the car.
            if (pos.x < o.x - Radius || pos.x > o.x + size.x + Radius || pos.z < o.z - Radius || pos.z > o.z + size.z + Radius) continue;
            if (n == 0) continue;
            var protos = td.treePrototypes;
            for (int i = 0; i < n; i++)
            {
                var ti = inst[i];
                var p = o + Vector3.Scale(ti.position, size);
                float dx = p.x - pos.x, dz = p.z - pos.z;
                if (dx * dx + dz * dz > Radius * Radius) continue;
                var (r, h, name) = Size(protos, ti.prototypeIndex);
                if (!_logged && !protoNames.Contains(name)) protoNames.Add($"{name} {r:0.0}x{h:0} m");
                float hh = h * Mathf.Max(0.2f, ti.heightScale);
                _near.Add(new Canopy
                {
                    Base = p, R = Mathf.Clamp(r * Mathf.Max(0.2f, ti.widthScale) * 0.85f, 0.5f, 8f),
                    Y0 = p.y + 0.3f * hh, Y1 = p.y + hh,
                });
            }
        }
        if (!_logged)
        {
            _logged = true;
            Plugin.Info($"trees: {total} in {terrainsWithTrees} of {terrains.Length} terrain(s), {_near.Count} within {Radius:0} m" +
                        (protoNames.Count > 0 ? $"; kinds near: {string.Join(", ", protoNames)}" : "") +
                        (total == 0 ? " (none in the terrain data: the camera can't see treetops)" : ""));
        }
    }

    // Tree model size (canopy radius, height) from its prefab's meshes; a plain tree if unreadable.
    static (float r, float h, string name) Size(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TreePrototype> protos, int index)
    {
        if (protos == null || index < 0 || index >= protos.Length) return (2.5f, 10f, "?");
        var proto = protos[index];
        var prefab = proto?.prefab;
        if (prefab == null) return (2.5f, 10f, "?");
        if (_protoSize.TryGetValue(prefab.Pointer, out var known)) return known;
        float r = 2.5f, h = 10f;
        try
        {
            var root = prefab.transform.position;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var b = mesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    var corner = m.MultiplyPoint3x4(new Vector3(c % 2 == 0 ? b.min.x : b.max.x, (c / 2) % 2 == 0 ? b.min.y : b.max.y,
                                                                c < 4 ? b.min.z : b.max.z)) - root;
                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                }
            }
            if (max.y > min.y)
            {
                h = Mathf.Clamp(max.y - Mathf.Min(0f, min.y), 2f, 60f);
                r = Mathf.Clamp(Mathf.Max(Mathf.Max(-min.x, max.x), Mathf.Max(-min.z, max.z)), 0.5f, 10f);
            }
        }
        catch { }
        var v = (r, h, prefab.name);
        _protoSize[prefab.Pointer] = v;
        return v;
    }

    // Does any treetop cross the line from a to b?
    public static bool Blocks(Vector3 a, Vector3 b)
    {
        if (_near.Count == 0) return false;
        const int samples = 14;
        foreach (var c in _near)
        {
            // Quick skip: the tree's circle against the line's box.
            if (c.Base.x + c.R < Mathf.Min(a.x, b.x) || c.Base.x - c.R > Mathf.Max(a.x, b.x) ||
                c.Base.z + c.R < Mathf.Min(a.z, b.z) || c.Base.z - c.R > Mathf.Max(a.z, b.z)) continue;
            for (int i = 1; i <= samples; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)samples);
                if (p.y < c.Y0 || p.y > c.Y1) continue;
                float dx = p.x - c.Base.x, dz = p.z - c.Base.z;
                if (dx * dx + dz * dz < c.R * c.R) return true;
            }
        }
        return false;
    }

    // How many tree trunks stand within r metres (for "among trees": the camera comes closer).
    public static int CountNear(Vector3 p, float r)
    {
        int n = 0;
        foreach (var c in _near)
        {
            float dx = c.Base.x - p.x, dz = c.Base.z - p.z;
            if (dx * dx + dz * dz < r * r) n++;
        }
        return n;
    }
}
