using System.Collections.Generic;
using System.IO;
using System.Linq;
using Thundergun.CodFormats;
using Thundergun.SEFormats;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// BO3's HD Thundergun (The Giant), exported with Greyhound: the view model and its viewmodel animations, turned into
    /// the same <see cref="CodModel"/> / <see cref="CodAnim"/> the BO1 port loads into, so the rest of the import (rig,
    /// retarget, controller) is shared.
    ///
    /// Greyhound's SEAnims hold each bone's transform relative to its parent but no hierarchy, and leave out the view
    /// bones above the arms. The hierarchy and the rest pose of anything an animation doesn't key come from BO1's
    /// arms rig (am_t5_thundergun: same bone names) for the arms and view, and from the HD model for the gun's parts.
    /// All maths here is in CoD space; Unity's Quaternion and Vector3 are used as plain maths.
    /// </summary>
    public static class TgBo3
    {
        public const string ExportRoot = @"E:\Greyhound boz\exported_files\black_ops_3";
        public const string ModelDir = ExportRoot + @"\xmodels\wpn_t7_zmb_thundergun_view";
        public const string ModelFile = ModelDir + @"\wpn_t7_zmb_thundergun_view_LOD0.semodel";
        public const string AnimDir = ExportRoot + @"\xanims";

        // Inches the glow cards move back towards the player (CoD -X is the gun's rear)
        private const float DecalLift = 0.03f;

        /// <summary>The gun attaches to the right hand's weapon tag with no offset, as in every CoD viewmodel.</summary>
        private const string GunParent = "tag_weapon_right";

        public static CodModel LoadModel()
        {
            SEModel se = SEModel.Load(ModelFile);
            var model = new CodModel();
            foreach (SEBone b in se.Bones)
            {
                var q = new Quaternion(b.GlobalRotation.X, b.GlobalRotation.Y, b.GlobalRotation.Z, b.GlobalRotation.W);
                model.Bones.Add(new CodBone { Name = b.Name, Parent = b.Parent, Rest = Pose(V(b.GlobalPosition), q) });
            }
            foreach (SEMesh m in se.Meshes)
            {
                int baseIndex = model.Vertices.Count;
                // The four grille-section glow cards lie in the grille's own plane (BO3 draws them on top as decals):
                // lift them a hair towards the player so they aren't lost under the grille
                bool decal = m.MaterialIndices.Length > 0 && se.Materials[m.MaterialIndices[0]].Name.Contains("_glow_");
                for (int v = 0; v < m.VertexCount; v++)
                {
                    var vertex = new CodVertex { Pos = m.Positions[v].ToCod() };
                    if (decal)
                        vertex.Pos.X -= DecalLift;
                    for (int i = 0; i < m.MaxInfluence; i++)
                    {
                        float w = m.WeightValues[v * m.MaxInfluence + i];
                        if (w > 0f)
                            vertex.Weights.Add((m.WeightBones[v * m.MaxInfluence + i], w));
                    }
                    if (vertex.Weights.Count == 0)
                        vertex.Weights.Add((0, 1f));
                    model.Vertices.Add(vertex);
                }
                int material = m.MaterialIndices.Length > 0 ? m.MaterialIndices[0] : 0;
                for (int f = 0; f < m.Faces.Length; f += 3)
                {
                    var face = new CodFace { Material = material };
                    for (int c = 0; c < 3; c++)
                    {
                        int v = m.Faces[f + c];
                        face.Corners[c] = new CodCorner
                        {
                            Vertex = baseIndex + v,
                            Normal = m.Normals != null ? new CodVec { X = m.Normals[v].X, Y = m.Normals[v].Y, Z = m.Normals[v].Z } : new CodVec { Z = 1 },
                            U = m.UVs != null ? m.UVs[v * 2] : 0f,
                            V = m.UVs != null ? m.UVs[v * 2 + 1] : 0f,
                        };
                    }
                    model.Faces.Add(face);
                }
            }
            foreach (SEMaterial m in se.Materials)
                model.Materials.Add(new CodMaterial { Name = m.Name, Type = "", Image = m.DiffuseMap });
            return model;
        }

        /// <summary>An HD viewmodel animation as world poses per frame, with its notetracks.</summary>
        public static CodAnim LoadAnim(string name, CodModel gun, CodModel bind, out List<(int frame, string note)> notes)
        {
            SEAnim se = SEAnim.Load(Path.Combine(AnimDir, name + ".seanim"));
            notes = se.Notes;

            // Hierarchy and rest (local) transforms: BO1's rig for arms and view, the HD model for the gun
            var parent = new Dictionary<string, string>();
            var restLocal = new Dictionary<string, (Vector3 p, Quaternion q)>();
            AddRig(bind, parent, restLocal);
            AddRig(gun, parent, restLocal);
            string gunRoot = gun.Bones[0].Name;
            parent[gunRoot] = GunParent;
            restLocal[gunRoot] = (Vector3.zero, Quaternion.identity);

            var tracks = se.Bones.ToDictionary(b => b.Name, b => b);
            // The gun's moving parts (pump, drums, arms, switches...) have their positions keyed as offsets from their
            // rest position on the model (the pump's slide on a shot is -8 from ~0); everything else is keyed in full
            var offsetKeyed = new HashSet<string>(gun.Bones.Skip(1).Select(b => b.Name));
            List<string> parts = parent.Keys.Union(parent.Values).Union(tracks.Keys).Distinct().ToList();
            var anim = new CodAnim { FrameRate = se.FrameRate > 0 ? se.FrameRate : 30f, Parts = parts };

            for (int f = 0; f < Mathf.Max(1, se.FrameCount); f++)
            {
                var world = new Dictionary<string, (Vector3 p, Quaternion q)>();
                (Vector3 p, Quaternion q) World(string bone)
                {
                    if (world.TryGetValue(bone, out var w))
                        return w;
                    (Vector3 p, Quaternion q) local = restLocal.TryGetValue(bone, out var rest) ? rest : (Vector3.zero, Quaternion.identity);
                    if (tracks.TryGetValue(bone, out SEAnimBone track))
                    {
                        bool offset = offsetKeyed.Contains(bone);
                        if (track.Positions.Count > 0)
                            local.p = offset ? local.p + Sample(track.Positions, f) : Sample(track.Positions, f);
                        // Rotations are full transforms for every bone (the angled ammo arms are keyed at their rest angle)
                        if (track.Rotations.Count > 0)
                            local.q = Sample(track.Rotations, f);
                    }
                    if (parent.TryGetValue(bone, out string up))
                    {
                        var pw = World(up);
                        w = (pw.p + pw.q * local.p, (pw.q * local.q).normalized);
                    }
                    else
                    {
                        w = local; // the view root
                    }
                    world[bone] = w;
                    return w;
                }
                anim.Frames.Add(parts.Select(b => { var w = World(b); return Pose(w.p, w.q); }).ToArray());
            }
            return anim;
        }

        private static void AddRig(CodModel model, Dictionary<string, string> parent, Dictionary<string, (Vector3, Quaternion)> restLocal)
        {
            foreach (CodBone b in model.Bones)
            {
                (Vector3 p, Quaternion q) = FromPose(b.Rest);
                if (b.Parent >= 0)
                {
                    CodBone up = model.Bones[b.Parent];
                    (Vector3 pp, Quaternion pq) = FromPose(up.Rest);
                    Quaternion inv = Quaternion.Inverse(pq);
                    parent[b.Name] = up.Name;
                    restLocal[b.Name] = (inv * (p - pp), (inv * q).normalized);
                }
                else if (!restLocal.ContainsKey(b.Name))
                {
                    restLocal[b.Name] = (p, q);
                }
            }
        }

        private static Vector3 Sample(List<SEKey<SEVec3>> keys, int frame)
        {
            int i = keys.FindLastIndex(k => k.Frame <= frame);
            if (i < 0) return V(keys[0].Value);
            if (i == keys.Count - 1 || keys[i].Frame == frame) return V(keys[i].Value);
            float t = (frame - keys[i].Frame) / (float)(keys[i + 1].Frame - keys[i].Frame);
            return Vector3.Lerp(V(keys[i].Value), V(keys[i + 1].Value), t);
        }

        private static Quaternion Sample(List<SEKey<SEQuat>> keys, int frame)
        {
            int i = keys.FindLastIndex(k => k.Frame <= frame);
            if (i < 0) return Q(keys[0].Value);
            if (i == keys.Count - 1 || keys[i].Frame == frame) return Q(keys[i].Value);
            float t = (frame - keys[i].Frame) / (float)(keys[i + 1].Frame - keys[i].Frame);
            return Quaternion.Slerp(Q(keys[i].Value), Q(keys[i + 1].Value), t);
        }

        // Greyhound exports in centimetres; CoD (and the rest of this import) works in inches
        private const float CmToInch = 1f / 2.54f;

        private static Vector3 V(SEVec3 v) => new Vector3(v.X, v.Y, v.Z) * CmToInch;
        private static Quaternion Q(SEQuat q) => new Quaternion(q.X, q.Y, q.Z, q.W).normalized;
        private static CodVec ToCod(this SEVec3 v) => new CodVec { X = v.X * CmToInch, Y = v.Y * CmToInch, Z = v.Z * CmToInch };

        /// <summary>CoD pose from a position and rotation: the rotated unit axes are the bone's X/Y/Z rows.</summary>
        private static CodPose Pose(Vector3 p, Quaternion q) => new CodPose
        {
            Pos = new CodVec { X = p.x, Y = p.y, Z = p.z },
            AxisX = C(q * Vector3.right), AxisY = C(q * Vector3.up), AxisZ = C(q * Vector3.forward),
        };

        /// <summary>A CoD pose's rotation: the rotation taking the unit axes onto its (right-handed) X/Y/Z rows.</summary>
        private static (Vector3, Quaternion) FromPose(CodPose pose)
        {
            Vector3 y = new Vector3(pose.AxisY.X, pose.AxisY.Y, pose.AxisY.Z), z = new Vector3(pose.AxisZ.X, pose.AxisZ.Y, pose.AxisZ.Z);
            return (new Vector3(pose.Pos.X, pose.Pos.Y, pose.Pos.Z), Quaternion.LookRotation(z, y));
        }

        private static CodVec C(Vector3 v) => new CodVec { X = v.x, Y = v.y, Z = v.z };
    }
}
