using System.Text.RegularExpressions;
using UnityEngine;

namespace EnemyBehaviorApi.Discovery
{
    /// <summary>
    /// Turns a live GameObject into the stable id that profiles and annotations key on.
    /// </summary>
    public static class EnemyIdentity
    {
        // Unity appends "(Clone)" on Instantiate and " (1)", " (2)" on scene duplicates.
        // Team Cherry's own object pooling adds numeric suffixes the same way. None of it
        // distinguishes one kind of enemy from another, so all of it comes off.
        private static readonly Regex Suffixes =
            new Regex(@"\s*\(Clone\)|\s*\(\d+\)\s*$|\s+\d+\s*$", RegexOptions.Compiled);

        /// <summary>
        /// The enemy id for a GameObject: its name with Unity's instance suffixes stripped.
        /// </summary>
        /// <remarks>
        /// Name-based identity is the only option that works without a per-enemy list, and
        /// it holds up because Silksong's enemy prefabs are consistently named. It has one
        /// real failure mode: two genuinely different enemies sharing a name, which an
        /// annotation file resolves by keying on the scene as well.
        /// </remarks>
        public static string For(GameObject go)
        {
            if (go == null) return null;
            string name = Suffixes.Replace(go.name, string.Empty).Trim();
            return name.Length == 0 ? go.name : name;
        }

        /// <summary>Transform path of <paramref name="descendant"/> relative to <paramref name="root"/>. Empty when they are the same object.</summary>
        public static string RelativePath(Transform root, Transform descendant)
        {
            if (root == null || descendant == null || root == descendant) return string.Empty;

            string path = descendant.name;
            Transform t = descendant.parent;
            while (t != null && t != root)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            // descendant was not actually under root - return the name alone rather than a
            // path that would never resolve.
            return t == null ? descendant.name : path;
        }

        /// <summary>Resolves a path produced by <see cref="RelativePath"/> back to a Transform. Null if it no longer exists.</summary>
        public static Transform Resolve(Transform root, string relativePath)
        {
            if (root == null) return null;
            return string.IsNullOrEmpty(relativePath) ? root : root.Find(relativePath);
        }
    }
}
