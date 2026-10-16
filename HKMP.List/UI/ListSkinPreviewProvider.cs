using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hkmp.Api.Client;
using UnityEngine;

namespace HKMP.List.UI
{
    internal static class ListSkinPreviewProvider
    {
        private static readonly object Gate = new object();

        private static readonly Dictionary<byte, Texture2D> CustomTextures =
            new Dictionary<byte, Texture2D>();

        private static readonly Dictionary<string, Sprite> Sprites =
            new Dictionary<string, Sprite>();

        private static readonly HashSet<Texture2D> OwnedTextures =
            new HashSet<Texture2D>();

        private static Dictionary<byte, string> _skinFiles;
        private static Texture2D _defaultSkinTexture;

        public static void CaptureDefaultTexture(IClientApi api)
        {
            FindDefaultTexture(api);
        }

        public static Sprite GetPreview(
            IClientApi api,
            byte skinId,
            HKMP.List.ListSkinPreviewMode mode)
        {
            string key =
                skinId.ToString() +
                ":" +
                mode.ToString();

            lock (Gate)
            {
                Sprite cached;
                if (Sprites.TryGetValue(key, out cached) &&
                    cached != null)
                {
                    return cached;
                }
            }

            Texture2D texture =
                GetSkinTexture(
                    api,
                    skinId);

            if (texture == null)
            {
                return null;
            }

            tk2dSpriteDefinition definition =
                GetReferenceDefinition(
                    api,
                    mode);

            Sprite sprite;

            if (definition != null)
            {
                Rect rect;

                if (TryGetTextureRect(
                        definition,
                        texture,
                        out rect))
                {
                    sprite =
                        Sprite.Create(
                            texture,
                            rect,
                            new Vector2(
                                0.5f,
                                0.5f),
                            64f);

                    sprite.name =
                        "HKMP.List.Skin." +
                        key;
                }
                else
                {
                    sprite =
                        CreateFullTextureSprite(
                            texture,
                            key);
                }
            }
            else
            {
                sprite =
                    CreateFullTextureSprite(
                        texture,
                        key);
            }

            if (sprite == null)
            {
                return null;
            }

            lock (Gate)
            {
                if (OwnedTextures.Contains(texture))
                {
                    texture.filterMode = FilterMode.Point;
                    texture.wrapMode = TextureWrapMode.Clamp;
                }

                Sprites[key] = sprite;
            }

            return sprite;
        }

        public static void ClearCache()
        {
            lock (Gate)
            {
                foreach (Sprite sprite in Sprites.Values)
                {
                    if (sprite != null)
                    {
                        UnityEngine.Object.Destroy(
                            sprite);
                    }
                }

                Sprites.Clear();

                foreach (Texture2D texture in OwnedTextures)
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(
                            texture);
                    }
                }

                OwnedTextures.Clear();
                CustomTextures.Clear();
                _skinFiles = null;
            }
        }

        private static Texture2D GetSkinTexture(
            IClientApi api,
            byte skinId)
        {
            if (skinId == 0)
            {
                return FindDefaultTexture(
                    api);
            }

            bool hasAuthoritativeSkinMap;
            Texture2D loadedSkinTexture = TryGetLoadedSkinTexture(
                api,
                skinId,
                out hasAuthoritativeSkinMap);

            if (hasAuthoritativeSkinMap)
            {
                if (loadedSkinTexture == null)
                {
                    return null;
                }

                lock (Gate)
                {
                    CustomTextures[skinId] = loadedSkinTexture;
                }

                return loadedSkinTexture;
            }

            lock (Gate)
            {
                Texture2D cached;
                if (CustomTextures.TryGetValue(
                        skinId,
                        out cached) &&
                    cached != null)
                {
                    return cached;
                }
            }

            string path =
                GetSkinFile(
                    skinId);

            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] bytes =
                    File.ReadAllBytes(
                        path);

                Texture2D texture =
                    new Texture2D(
                        2,
                        2,
                        TextureFormat.RGBA32,
                        false);

                if (!texture.LoadImage(
                        bytes,
                        false))
                {
                    UnityEngine.Object.Destroy(
                        texture);

                    return null;
                }

                texture.name =
                    "HKMP.List.Skin." +
                    skinId;

                lock (Gate)
                {
                    CustomTextures[skinId] =
                        texture;

                    OwnedTextures.Add(
                        texture);
                }

                return texture;
            }
            catch
            {
                return null;
            }
        }

        private static string GetSkinFile(
            byte skinId)
        {
            lock (Gate)
            {
                if (_skinFiles == null)
                {
                    _skinFiles =
                        BuildSkinFileMap();
                }

                string path;

                return
                    _skinFiles.TryGetValue(
                        skinId,
                        out path)
                        ? path
                        : null;
            }
        }

        private static Dictionary<byte, string> BuildSkinFileMap()
        {
            Dictionary<byte, string> result =
                new Dictionary<byte, string>();

            foreach (string root in GetSkinRoots())
            {
                if (string.IsNullOrWhiteSpace(root) ||
                    !Directory.Exists(root))
                {
                    continue;
                }

                string[] directories;

                try
                {
                    directories =
                        Directory.GetDirectories(
                            root)
                            .OrderBy(
                                value =>
                                    value,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (string directory in directories)
                {
                    string idPath =
                        Path.Combine(
                            directory,
                            "id.txt");

                    string texturePath =
                        Path.Combine(
                            directory,
                            "Knight.png");

                    if (!File.Exists(idPath) ||
                        !File.Exists(texturePath))
                    {
                        continue;
                    }

                    try
                    {
                        string text =
                            File.ReadAllText(
                                idPath)
                                .Trim();

                        int id;

                        if (!int.TryParse(
                                text,
                                out id) ||
                            id < 1 ||
                            id > 255)
                        {
                            continue;
                        }

                        byte skinId =
                            (byte)id;

                        if (!result.ContainsKey(
                                skinId))
                        {
                            result[skinId] =
                                texturePath;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> GetSkinRoots()
        {
            string dataPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                yield break;
            }
            yield return Path.Combine(dataPath, "Managed", "Mods", "HKMP", "Skins");
            yield return Path.Combine(
                dataPath, "Resources", "Data", "Managed", "Mods", "HKMP", "Skins");

            string gameRoot = Path.GetDirectoryName(dataPath);
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                yield return Path.Combine(gameRoot, "Mods", "HKMP", "Skins");
                yield return Path.Combine(gameRoot, "HKMP", "Skins");
            }

            yield return Path.Combine(dataPath, "Mods", "HKMP", "Skins");
        }

        private static Texture2D FindDefaultTexture(
            IClientApi api)
        {
            lock (Gate)
            {
                if (_defaultSkinTexture != null)
                {
                    return _defaultSkinTexture;
                }
            }

            Texture2D texture = TryGetHkmpDefaultTexture(api);

            if (texture == null)
            {
                try
                {
                    if (api != null &&
                        api.ClientManager != null &&
                        api.ClientManager.Players != null)
                    {
                        foreach (IClientPlayer player in api.ClientManager.Players)
                        {
                            if (player == null ||
                                player.SkinId != 0 ||
                                player.PlayerObject == null)
                            {
                                continue;
                            }

                            texture = GetAnimationTexture(player.PlayerObject, "Idle");
                            if (texture != null)
                            {
                                break;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (texture == null)
            {
                try
                {
                    if (HeroController.instance != null)
                    {
                        texture = GetAnimationTexture(
                            HeroController.instance.gameObject,
                            "Idle");
                    }
                }
                catch
                {
                }
            }

            if (texture != null)
            {
                lock (Gate)
                {
                    if (_defaultSkinTexture == null)
                    {
                        _defaultSkinTexture = texture;
                    }
                    return _defaultSkinTexture;
                }
            }

            return null;
        }

        private static Texture2D TryGetLoadedSkinTexture(
            IClientApi api,
            byte skinId,
            out bool hasAuthoritativeSkinMap)
        {
            hasAuthoritativeSkinMap = false;

            try
            {
                if (api == null || api.ClientManager == null)
                {
                    return null;
                }

                object playerManager = ReadPrivateMember(
                    api.ClientManager,
                    "_playerManager");

                object skinManager = ReadPrivateMember(
                    playerManager,
                    "_skinManager");

                object value = ReadPrivateMember(
                    skinManager,
                    "_playerSkins");

                IDictionary skins = value as IDictionary;
                if (skins == null)
                {
                    return null;
                }

                hasAuthoritativeSkinMap = true;

                object playerSkin = skins[(object)skinId];
                if (playerSkin == null)
                {
                    return null;
                }

                return ReadPrivateMember(
                    playerSkin,
                    "KnightTexture") as Texture2D;
            }
            catch
            {
                hasAuthoritativeSkinMap = false;
                return null;
            }
        }

        private static Texture2D TryGetHkmpDefaultTexture(IClientApi api)
        {
            try
            {
                if (api == null || api.ClientManager == null)
                {
                    return null;
                }

                object playerManager = ReadPrivateMember(
                    api.ClientManager,
                    "_playerManager");

                object skinManager = ReadPrivateMember(
                    playerManager,
                    "_skinManager");

                object defaultPlayerSkin = ReadPrivateMember(
                    skinManager,
                    "_defaultPlayerSkin");

                object value = ReadPrivateMember(
                    defaultPlayerSkin,
                    "KnightTexture");

                return value as Texture2D;
            }
            catch
            {
                return null;
            }
        }

        private static object ReadPrivateMember(
            object target,
            string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            for (Type current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo property = current.GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
            }

            return null;
        }

        private static GameObject FindReferencePlayer(
            IClientApi api)
        {
            try
            {
                if (HeroController.instance != null)
                {
                    return HeroController.instance.gameObject;
                }
            }
            catch
            {
            }

            try
            {
                if (api != null &&
                    api.ClientManager != null &&
                    api.ClientManager.Players != null)
                {
                    foreach (IClientPlayer player
                             in api.ClientManager.Players)
                    {
                        if (player != null &&
                            player.PlayerObject != null)
                        {
                            return player.PlayerObject;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static tk2dSpriteDefinition GetReferenceDefinition(
            IClientApi api,
            HKMP.List.ListSkinPreviewMode mode)
        {
            GameObject player =
                FindReferencePlayer(
                    api);

            if (player == null)
            {
                return null;
            }

            try
            {
                tk2dSpriteAnimator animator =
                    player.GetComponent<
                        tk2dSpriteAnimator>();

                if (animator == null)
                {
                    return null;
                }

                string[] clipNames =
                    mode ==
                    HKMP.List.ListSkinPreviewMode.Bench
                        ? new[]
                        {
                            "Sit",
                            "Sit Lean",
                            "Sit Idle",
                            "Sitting Asleep"
                        }
                        : new[]
                        {
                            "Idle"
                        };

                for (int clipIndex = 0;
                     clipIndex < clipNames.Length;
                     clipIndex++)
                {
                    tk2dSpriteAnimationClip clip =
                        animator.GetClipByName(
                            clipNames[clipIndex]);

                    tk2dSpriteDefinition definition =
                        GetFirstFrameDefinition(
                            clip);

                    if (definition != null)
                    {
                        return definition;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static Texture2D GetAnimationTexture(
            GameObject player,
            string clipName)
        {
            if (player == null)
            {
                return null;
            }

            try
            {
                tk2dSpriteAnimator animator =
                    player.GetComponent<
                        tk2dSpriteAnimator>();

                if (animator == null)
                {
                    return null;
                }

                tk2dSpriteAnimationClip clip =
                    animator.GetClipByName(
                        clipName);

                tk2dSpriteDefinition definition =
                    GetFirstFrameDefinition(
                        clip);

                if (definition == null ||
                    definition.material == null)
                {
                    return null;
                }

                return
                    definition.material.mainTexture as Texture2D;
            }
            catch
            {
                return null;
            }
        }

        private static tk2dSpriteDefinition GetFirstFrameDefinition(
            tk2dSpriteAnimationClip clip)
        {
            if (clip == null ||
                clip.frames == null ||
                clip.frames.Length == 0)
            {
                return null;
            }

            tk2dSpriteAnimationFrame frame =
                clip.frames[0];

            if (frame == null ||
                frame.spriteCollection == null ||
                frame.spriteCollection.spriteDefinitions == null ||
                frame.spriteCollection.spriteDefinitions.Length == 0)
            {
                return null;
            }

            int spriteId =
                frame.spriteId;

            if (spriteId < 0 ||
                spriteId >= frame.spriteCollection.spriteDefinitions.Length)
            {
                return null;
            }

            return
                frame.spriteCollection
                    .spriteDefinitions[spriteId];
        }

        private static bool TryGetTextureRect(
            tk2dSpriteDefinition definition,
            Texture2D texture,
            out Rect rect)
        {
            rect =
                new Rect();

            if (definition == null ||
                texture == null ||
                texture.width <= 0 ||
                texture.height <= 0 ||
                definition.uvs == null ||
                definition.uvs.Length == 0)
            {
                return false;
            }

            float minX = 1f;
            float maxX = 0f;
            float minY = 1f;
            float maxY = 0f;

            foreach (Vector2 uv in definition.uvs)
            {
                minX =
                    Mathf.Min(
                        minX,
                        uv.x);

                maxX =
                    Mathf.Max(
                        maxX,
                        uv.x);

                minY =
                    Mathf.Min(
                        minY,
                        uv.y);

                maxY =
                    Mathf.Max(
                        maxY,
                        uv.y);
            }

            minX =
                Mathf.Clamp01(
                    minX);

            maxX =
                Mathf.Clamp01(
                    maxX);

            minY =
                Mathf.Clamp01(
                    minY);

            maxY =
                Mathf.Clamp01(
                    maxY);

            if (maxX <= minX ||
                maxY <= minY)
            {
                return false;
            }

            int x =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        minX *
                        texture.width),
                    0,
                    texture.width - 1);

            int y =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        minY *
                        texture.height),
                    0,
                    texture.height - 1);

            int right =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        maxX *
                        texture.width),
                    x + 1,
                    texture.width);

            int top =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        maxY *
                        texture.height),
                    y + 1,
                    texture.height);

            rect =
                new Rect(
                    x,
                    y,
                    right - x,
                    top - y);

            return
                rect.width > 0f &&
                rect.height > 0f;
        }

        private static Sprite CreateFullTextureSprite(
            Texture2D texture,
            string key)
        {
            if (texture == null ||
                texture.width <= 0 ||
                texture.height <= 0)
            {
                return null;
            }

            try
            {
                Sprite sprite =
                    Sprite.Create(
                        texture,
                        new Rect(
                            0f,
                            0f,
                            texture.width,
                            texture.height),
                        new Vector2(
                            0.5f,
                            0.5f),
                        64f);

                sprite.name =
                    "HKMP.List.Skin.Full." +
                    key;

                return sprite;
            }
            catch
            {
                return null;
            }
        }
    }
}
