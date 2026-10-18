using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SunMirror;

/// <summary>
/// Минимальный самодостаточный загрузчик .glb (glTF 2.0 binary) в рантайме.
/// Строит статические меши + PBR-материалы (URP Lit / Standard). Без сторонних библиотек.
/// v1: цель — «модель появляется в игре». Ориентацию/материалы докручиваем по скринам.
/// </summary>
public static class GlbLoader
{
    const uint MAGIC = 0x46546C67; // 'glTF'
    const uint CHUNK_JSON = 0x4E4F534A;
    const uint CHUNK_BIN = 0x004E4942;

    public static GameObject? Load(byte[] glb, string rootName)
    {
        try
        {
            if (glb.Length < 12 || BitConverter.ToUInt32(glb, 0) != MAGIC)
            { Plugin.Log.LogError("GlbLoader: not a glb file."); return null; }

            JObject? gltf = null;
            byte[]? bin = null;
            int p = 12;
            while (p + 8 <= glb.Length)
            {
                uint clen = BitConverter.ToUInt32(glb, p);
                uint ctype = BitConverter.ToUInt32(glb, p + 4);
                int cstart = p + 8;
                if (ctype == CHUNK_JSON)
                    gltf = JObject.Parse(System.Text.Encoding.UTF8.GetString(glb, cstart, (int)clen));
                else if (ctype == CHUNK_BIN)
                { bin = new byte[clen]; Array.Copy(glb, cstart, bin, 0, (int)clen); }
                p = cstart + (int)clen;
            }
            if (gltf == null || bin == null)
            { Plugin.Log.LogError("GlbLoader: missing JSON/BIN chunk."); return null; }

            var ctx = new Ctx(gltf, bin);
            var root = new GameObject(rootName);

            var scenes = gltf["scenes"] as JArray;
            int sceneIdx = (int?)gltf["scene"] ?? 0;
            if (scenes != null && sceneIdx < scenes.Count && scenes[sceneIdx]["nodes"] is JArray sceneNodes)
                foreach (var ni in sceneNodes)
                    BuildNode(ctx, (int)ni, root.transform);

            Plugin.Log.LogInfo($"GlbLoader: built {ctx.MeshCount} meshes, {ctx.mats.Length} materials.");
            return root;
        }
        catch (Exception e) { Plugin.Log.LogError($"GlbLoader failed: {e}"); return null; }
    }

    sealed class Ctx
    {
        public readonly JObject gltf;
        public readonly byte[] bin;
        public readonly JArray accessors, bufferViews, meshes, materials, nodes, images, textures;
        public readonly Material?[] mats;
        public readonly Texture2D?[] texCache;
        public int MeshCount;
        public Ctx(JObject g, byte[] b)
        {
            gltf = g; bin = b;
            accessors = g["accessors"] as JArray ?? new JArray();
            bufferViews = g["bufferViews"] as JArray ?? new JArray();
            meshes = g["meshes"] as JArray ?? new JArray();
            materials = g["materials"] as JArray ?? new JArray();
            nodes = g["nodes"] as JArray ?? new JArray();
            images = g["images"] as JArray ?? new JArray();
            textures = g["textures"] as JArray ?? new JArray();
            mats = new Material?[materials.Count];
            texCache = new Texture2D?[images.Count];
        }
    }

    static void BuildNode(Ctx ctx, int index, Transform parent)
    {
        var n = ctx.nodes[index];
        var go = new GameObject((string?)n["name"] ?? $"node{index}");
        go.transform.SetParent(parent, false);

        if (n["matrix"] is JArray m && m.Count == 16)
        {
            var mm = new Matrix4x4();
            for (int i = 0; i < 16; i++) mm[i % 4, i / 4] = (float)m[i]; // column-major
            go.transform.localPosition = mm.GetColumn(3);
            var fwd = (Vector3)mm.GetColumn(2); var up = (Vector3)mm.GetColumn(1);
            if (fwd.sqrMagnitude > 1e-6f && up.sqrMagnitude > 1e-6f) go.transform.localRotation = Quaternion.LookRotation(fwd, up);
            go.transform.localScale = new Vector3(((Vector3)mm.GetColumn(0)).magnitude, ((Vector3)mm.GetColumn(1)).magnitude, ((Vector3)mm.GetColumn(2)).magnitude);
        }
        else
        {
            if (n["translation"] is JArray t) go.transform.localPosition = new Vector3((float)t[0], (float)t[1], (float)t[2]);
            if (n["rotation"] is JArray r) go.transform.localRotation = new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
            if (n["scale"] is JArray s) go.transform.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
        }

        if ((int?)n["mesh"] is int meshIdx) BuildMesh(ctx, meshIdx, go);
        if (n["children"] is JArray ch) foreach (var c in ch) BuildNode(ctx, (int)c, go.transform);
    }

    static void BuildMesh(Ctx ctx, int meshIndex, GameObject go)
    {
        var prims = ctx.meshes[meshIndex]["primitives"] as JArray;
        if (prims == null) return;
        int sub = 0;
        foreach (var prim in prims)
        {
            try
            {
                var attr = prim["attributes"];
                if (attr?["POSITION"] == null) continue;
                var verts = ReadVec3(ctx, (int)attr["POSITION"]!);
                Vector3[]? normals = attr["NORMAL"] != null ? ReadVec3(ctx, (int)attr["NORMAL"]!) : null;
                Vector2[]? uvs = attr["TEXCOORD_0"] != null ? ReadVec2(ctx, (int)attr["TEXCOORD_0"]!) : null;
                int[] tris = prim["indices"] != null ? ReadIndices(ctx, (int)prim["indices"]!) : SequentialIndices(verts.Length);

                var mesh = new Mesh { name = $"{go.name}_prim{sub}" };
                if (verts.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                if (normals != null && normals.Length == verts.Length) mesh.normals = normals;
                if (uvs != null && uvs.Length == verts.Length) mesh.uv = uvs;
                mesh.triangles = tris;
                if (normals == null) mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var child = new GameObject($"{go.name}_prim{sub}");
                child.transform.SetParent(go.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = child.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetMaterial(ctx, (int?)prim["material"]);
                ctx.MeshCount++;
                sub++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"GlbLoader: primitive skipped: {e.Message}"); }
        }
    }

    // ---------- materials ----------
    static Material GetMaterial(Ctx ctx, int? matIndex)
    {
        if (matIndex is int mi && mi >= 0 && mi < ctx.mats.Length)
        {
            if (ctx.mats[mi] != null) return ctx.mats[mi]!;
            var mat = BuildMaterial(ctx, mi);
            ctx.mats[mi] = mat;
            return mat;
        }
        return DefaultMaterial();
    }

    static Shader FindShader()
        => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");

    static Material DefaultMaterial() => new Material(FindShader()) { name = "glb_default" };

    static Material BuildMaterial(Ctx ctx, int mi)
    {
        var jm = ctx.materials[mi];
        var mat = new Material(FindShader()) { name = (string?)jm["name"] ?? $"mat{mi}" };
        bool urp = mat.shader.name.Contains("Universal");

        var pbr = jm["pbrMetallicRoughness"];
        Color baseColor = Color.white;
        if (pbr?["baseColorFactor"] is JArray bcf && bcf.Count >= 4)
            baseColor = new Color((float)bcf[0], (float)bcf[1], (float)bcf[2], (float)bcf[3]);

        float metallic = (float?)pbr?["metallicFactor"] ?? 1f;
        float roughness = (float?)pbr?["roughnessFactor"] ?? 1f;

        if (urp) { mat.SetColor("_BaseColor", baseColor); mat.SetFloat("_Metallic", metallic); mat.SetFloat("_Smoothness", 1f - roughness); }
        else { mat.color = baseColor; mat.SetFloat("_Metallic", metallic); mat.SetFloat("_Glossiness", 1f - roughness); }

        // baseColor texture (guarded)
        try
        {
            if (pbr?["baseColorTexture"]?["index"] is JToken ti)
            {
                int texIndex = (int)ti;
                if (texIndex >= 0 && texIndex < ctx.textures.Count && (int?)ctx.textures[texIndex]["source"] is int imgIdx)
                {
                    var tex = LoadImage(ctx, imgIdx);
                    if (tex != null) mat.SetTexture(urp ? "_BaseMap" : "_MainTex", tex);
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"GlbLoader: texture skipped for mat {mi}: {e.Message}"); }

        // двусторонний рендер — компенсирует возможную инверсию граней без Unity-редактора
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        return mat;
    }

    static Texture2D? LoadImage(Ctx ctx, int imgIdx)
    {
        if (imgIdx < 0 || imgIdx >= ctx.images.Count) return null;
        if (ctx.texCache[imgIdx] != null) return ctx.texCache[imgIdx];
        var img = ctx.images[imgIdx];
        if ((int?)img["bufferView"] is not int bvIdx) return null; // только embedded bufferView
        byte[] data = ReadBufferView(ctx, bvIdx);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!tex.LoadImage(data)) return null;
        ctx.texCache[imgIdx] = tex;
        return tex;
    }

    // ---------- accessor readers ----------
    static byte[] ReadBufferView(Ctx ctx, int bvIndex)
    {
        var bv = ctx.bufferViews[bvIndex];
        int off = (int?)bv["byteOffset"] ?? 0;
        int len = (int)bv["byteLength"]!;
        var outb = new byte[len];
        Array.Copy(ctx.bin, off, outb, 0, len);
        return outb;
    }

    static void AccessorInfo(Ctx ctx, int accIndex, out JToken acc, out int bvOffset, out int stride,
                             out int compType, out int count, out int numComp)
    {
        acc = ctx.accessors[accIndex];
        int bvIndex = (int)acc["bufferView"]!;
        var bv = ctx.bufferViews[bvIndex];
        int bvByteOffset = (int?)bv["byteOffset"] ?? 0;
        int accByteOffset = (int?)acc["byteOffset"] ?? 0;
        bvOffset = bvByteOffset + accByteOffset;
        compType = (int)acc["componentType"]!;
        count = (int)acc["count"]!;
        numComp = TypeComponents((string)acc["type"]!);
        int bvStride = (int?)bv["byteStride"] ?? 0;
        stride = bvStride > 0 ? bvStride : numComp * ComponentSize(compType);
    }

    static Vector3[] ReadVec3(Ctx ctx, int accIndex)
    {
        AccessorInfo(ctx, accIndex, out _, out int off, out int stride, out int ct, out int count, out int nc);
        var res = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int b = off + i * stride;
            res[i] = new Vector3(ReadFloat(ctx.bin, b, ct), ReadFloat(ctx.bin, b + ComponentSize(ct), ct), ReadFloat(ctx.bin, b + 2 * ComponentSize(ct), ct));
        }
        return res;
    }

    static Vector2[] ReadVec2(Ctx ctx, int accIndex)
    {
        AccessorInfo(ctx, accIndex, out _, out int off, out int stride, out int ct, out int count, out int nc);
        var res = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int b = off + i * stride;
            res[i] = new Vector2(ReadFloat(ctx.bin, b, ct), ReadFloat(ctx.bin, b + ComponentSize(ct), ct));
        }
        return res;
    }

    static int[] ReadIndices(Ctx ctx, int accIndex)
    {
        AccessorInfo(ctx, accIndex, out _, out int off, out int stride, out int ct, out int count, out int nc);
        int cs = ComponentSize(ct);
        var res = new int[count];
        for (int i = 0; i < count; i++)
        {
            int b = off + i * (stride > 0 ? stride : cs);
            res[i] = ct switch
            {
                5121 => ctx.bin[b],
                5123 => BitConverter.ToUInt16(ctx.bin, b),
                5125 => (int)BitConverter.ToUInt32(ctx.bin, b),
                _ => BitConverter.ToUInt16(ctx.bin, b),
            };
        }
        return res;
    }

    static int[] SequentialIndices(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = i; return a; }

    static float ReadFloat(byte[] buf, int b, int compType) => compType switch
    {
        5126 => BitConverter.ToSingle(buf, b),
        5123 => BitConverter.ToUInt16(buf, b),
        5122 => BitConverter.ToInt16(buf, b),
        5121 => buf[b],
        5120 => (sbyte)buf[b],
        _ => BitConverter.ToSingle(buf, b),
    };

    static int ComponentSize(int ct) => ct switch { 5120 => 1, 5121 => 1, 5122 => 2, 5123 => 2, 5125 => 4, 5126 => 4, _ => 4 };
    static int TypeComponents(string t) => t switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 3 };
}
