// tribe core: the part that runs inside Green Hell. tribe.exe injects it through Mono's embedding API. It lifts
// the co-op player limit (Patch.cs) and nothing else. Written in C# 5 so it builds with the csc.exe that ships
// with Windows.
using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace Tribe
{
    public static class Entry
    {
        static string pending;
        static GameObject go;
        static readonly Camera.CameraCallback hook = OnMainThread;

        /// <summary>
        /// Called by tribe.exe via mono_runtime_invoke, on the injector's own thread, with "port|lobby size|token". No Unity
        /// API may be touched here, so this only hooks Camera.onPreCull/onPreRender: plain static delegate fields, safe
        /// to assign from any thread, fired on the main thread by the next rendered frame (menus included).
        /// Running tribe.exe again on an already patched game lands here again (Mono hands back this same assembly):
        /// the patch is already in, so that only reconnects the status link and takes the new lobby size.
        /// </summary>
        public static void Inject(string arg)
        {
            Interlocked.Exchange(ref pending, arg);
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, hook);
            Camera.onPreRender = (Camera.CameraCallback)Delegate.Combine(Camera.onPreRender, hook);
        }

        static void OnMainThread(Camera unused)
        {
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, hook);
            Camera.onPreRender = (Camera.CameraCallback)Delegate.Remove(Camera.onPreRender, hook);
            string arg = Interlocked.Exchange(ref pending, null);
            if (arg == null) return;
            try
            {
                var parts = arg.Split('|');
                int port = int.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts.Length > 1) Patch.Wanted = Mathf.Clamp(int.Parse(parts[1], CultureInfo.InvariantCulture), Patch.Vanilla, Patch.Capacity);
                if (go == null)
                {
                    // A newer tribe build is a new assembly with its own runner. Retire any older one first, or two
                    // runners would both keep setting the lobby limit, each to its own idea of the size.
                    foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>())
                        if (old != null && old.name == "Tribe" && old.hideFlags == HideFlags.HideAndDontSave) { old.SetActive(false); UnityEngine.Object.Destroy(old); }
                    go = new GameObject("Tribe");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.AddComponent<Runner>();
                }
                go.GetComponent<Runner>().Attach(port, parts.Length > 2 ? parts[2] : "");
            }
            catch (Exception e) { Debug.LogError("[tribe] start failed: " + e); }
        }
    }
}
