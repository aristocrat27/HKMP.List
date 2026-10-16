using System;
using UnityEngine;
using UnityEngine.UI;

namespace HKMP.List.UI
{
    internal static class ListUiFactory
    {
        private static Font _font;
        private static Sprite _whiteSprite;
        private static Sprite _gearSprite;

        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font =
                        Resources.GetBuiltinResource<Font>(
                            "Arial.ttf");
                }

                return _font;
            }
        }

        public static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite == null)
                {
                    _whiteSprite =
                        Sprite.Create(
                            Texture2D.whiteTexture,
                            new Rect(
                                0f,
                                0f,
                                1f,
                                1f),
                            new Vector2(
                                0.5f,
                                0.5f),
                            1f);
                }

                return _whiteSprite;
            }
        }

        public static Sprite GearSprite
        {
            get
            {
                if (_gearSprite == null)
                {
                    _gearSprite =
                        CreateGearSprite();
                }

                return _gearSprite;
            }
        }

        public static GameObject CreateObject(
            Transform parent,
            string name)
        {
            GameObject gameObject =
                new GameObject(
                    name,
                    typeof(RectTransform));

            gameObject.transform.SetParent(
                parent,
                false);

            return gameObject;
        }

        public static RectTransform GetRect(
            GameObject gameObject)
        {
            return gameObject.GetComponent<RectTransform>();
        }

        public static Image CreateImage(
            Transform parent,
            string name,
            Color color,
            bool raycastTarget = false)
        {
            GameObject gameObject =
                CreateObject(
                    parent,
                    name);

            Image image =
                gameObject.AddComponent<Image>();

            image.sprite =
                WhiteSprite;

            image.color =
                color;

            image.raycastTarget =
                raycastTarget;

            return image;
        }

        public static Text CreateText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            Color color,
            TextAnchor alignment =
                TextAnchor.MiddleLeft)
        {
            GameObject gameObject =
                CreateObject(
                    parent,
                    name);

            Text text =
                gameObject.AddComponent<Text>();

            text.font =
                Font;

            text.text =
                value ?? string.Empty;

            text.fontSize =
                fontSize;

            text.color =
                color;

            text.alignment =
                alignment;

            text.fontStyle =
                FontStyle.Normal;

            text.resizeTextForBestFit =
                false;

            text.supportRichText =
                false;

            text.alignByGeometry =
                false;

            text.horizontalOverflow =
                HorizontalWrapMode.Wrap;

            text.verticalOverflow =
                VerticalWrapMode.Truncate;

            text.raycastTarget =
                false;

            return text;
        }

        public static Button CreateButton(
            Transform parent,
            string name,
            string caption,
            UnityEngine.Events.UnityAction onClick,
            int fontSize = 18)
        {
            GameObject gameObject =
                CreateObject(
                    parent,
                    name);

            Image background =
                gameObject.AddComponent<Image>();

            background.sprite =
                WhiteSprite;

            background.color =
                new Color(
                    0.12f,
                    0.12f,
                    0.15f,
                    0.96f);

            Button button =
                gameObject.AddComponent<Button>();

            button.targetGraphic =
                background;

            ColorBlock colors =
                button.colors;

            colors.normalColor =
                background.color;

            colors.highlightedColor =
                new Color(
                    0.20f,
                    0.20f,
                    0.24f,
                    0.98f);

            colors.pressedColor =
                new Color(
                    0.08f,
                    0.08f,
                    0.10f,
                    1f);

            colors.disabledColor =
                new Color(
                    0.08f,
                    0.08f,
                    0.08f,
                    0.50f);

            colors.colorMultiplier =
                1f;

            button.colors =
                colors;

            if (onClick != null)
            {
                button.onClick.AddListener(
                    onClick);
            }

            Text text =
                CreateText(
                    gameObject.transform,
                    "Text",
                    caption,
                    fontSize,
                    Color.white,
                    TextAnchor.MiddleCenter);

            RectTransform textRect =
                text.rectTransform;

            textRect.anchorMin =
                Vector2.zero;

            textRect.anchorMax =
                Vector2.one;

            textRect.offsetMin =
                new Vector2(
                    6f,
                    2f);

            textRect.offsetMax =
                new Vector2(
                    -6f,
                    -2f);

            return button;
        }

        public static Button CreateIconButton(
            Transform parent,
            string name,
            Sprite icon,
            UnityEngine.Events.UnityAction onClick)
        {
            Button button =
                CreateButton(
                    parent,
                    name,
                    string.Empty,
                    onClick,
                    18);

            Text text =
                button.GetComponentInChildren<Text>();

            if (text != null)
            {
                UnityEngine.Object.Destroy(
                    text.gameObject);
            }

            GameObject iconObject =
                CreateObject(
                    button.transform,
                    "Icon");

            RectTransform iconRect =
                iconObject.GetComponent<RectTransform>();

            iconRect.anchorMin =
                new Vector2(
                    0.5f,
                    0.5f);

            iconRect.anchorMax =
                new Vector2(
                    0.5f,
                    0.5f);

            iconRect.pivot =
                new Vector2(
                    0.5f,
                    0.5f);

            iconRect.sizeDelta =
                new Vector2(
                    25f,
                    25f);

            iconRect.anchoredPosition =
                Vector2.zero;

            Image image =
                iconObject.AddComponent<Image>();

            image.sprite =
                icon;

            image.preserveAspect =
                true;

            image.raycastTarget =
                false;

            return button;
        }

        public static InputField CreateInputField(
            Transform parent,
            string name,
            string value,
            string placeholder,
            int fontSize,
            UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            GameObject gameObject =
                CreateObject(
                    parent,
                    name);

            Image background =
                gameObject.AddComponent<Image>();

            background.sprite =
                WhiteSprite;

            background.color =
                new Color(
                    0.07f,
                    0.07f,
                    0.09f,
                    0.95f);

            InputField inputField =
                gameObject.AddComponent<InputField>();

            inputField.targetGraphic =
                background;

            inputField.transition =
                Selectable.Transition.ColorTint;

            inputField.navigation =
                new Navigation
                {
                    mode = Navigation.Mode.Automatic
                };

            Text text =
                CreateText(
                    gameObject.transform,
                    "Text",
                    value ?? string.Empty,
                    fontSize,
                    Color.white,
                    TextAnchor.UpperLeft);

            text.horizontalOverflow =
                HorizontalWrapMode.Wrap;

            text.verticalOverflow =
                VerticalWrapMode.Overflow;

            text.supportRichText =
                false;

            text.alignByGeometry =
                false;

            text.resizeTextForBestFit =
                false;

            RectTransform textRect =
                text.rectTransform;

            textRect.anchorMin =
                Vector2.zero;

            textRect.anchorMax =
                Vector2.one;

            textRect.offsetMin =
                new Vector2(
                    9f,
                    7f);

            textRect.offsetMax =
                new Vector2(
                    -9f,
                    -7f);

            Text placeholderText =
                CreateText(
                    gameObject.transform,
                    "Placeholder",
                    placeholder ?? string.Empty,
                    fontSize,
                    new Color(
                        0.55f,
                        0.55f,
                        0.60f,
                        1f),
                    TextAnchor.UpperLeft);

            placeholderText.fontStyle =
                FontStyle.Italic;

            placeholderText.supportRichText =
                false;

            placeholderText.alignByGeometry =
                false;

            RectTransform placeholderRect =
                placeholderText.rectTransform;

            placeholderRect.anchorMin =
                Vector2.zero;

            placeholderRect.anchorMax =
                Vector2.one;

            placeholderRect.offsetMin =
                new Vector2(
                    9f,
                    7f);

            placeholderRect.offsetMax =
                new Vector2(
                    -9f,
                    -7f);

            inputField.textComponent =
                text;

            inputField.placeholder =
                placeholderText;

            inputField.text =
                value ?? string.Empty;

            inputField.lineType =
                InputField.LineType.MultiLineNewline;

            inputField.contentType =
                InputField.ContentType.Standard;

            inputField.characterLimit =
                HKMP.List.Shared.ListProtocol.MaxNoteLength;

            inputField.shouldHideMobileInput =
                true;

            if (onValueChanged != null)
            {
                inputField.onValueChanged.AddListener(
                    onValueChanged);
            }

            return inputField;
        }

        private static Sprite CreateGearSprite()
        {
            const int size = 64;

            Texture2D texture =
                new Texture2D(
                    size,
                    size,
                    TextureFormat.RGBA32,
                    false);

            texture.name =
                "HKMP.List.GearTexture";

            texture.filterMode =
                FilterMode.Bilinear;

            texture.wrapMode =
                TextureWrapMode.Clamp;

            Color32[] pixels =
                new Color32[
                    size * size];

            float center =
                (size - 1) / 2f;

            for (int y = 0;
                 y < size;
                 y++)
            {
                for (int x = 0;
                     x < size;
                     x++)
                {
                    float dx =
                        x - center;

                    float dy =
                        y - center;

                    float radius =
                        Mathf.Sqrt(
                            dx * dx +
                            dy * dy);

                    float angle =
                        Mathf.Atan2(
                            dy,
                            dx);

                    float sector =
                        Mathf.Repeat(
                            angle +
                            Mathf.PI /
                            8f,
                            Mathf.PI /
                            4f) -
                        Mathf.PI /
                        8f;

                    float halfSector =
                        Mathf.Abs(
                            sector);

                    float outerRadius =
                        halfSector <
                        0.115f
                            ? 29f
                            : 24f;

                    bool body =
                        radius <=
                            outerRadius &&
                        radius >=
                            12f;

                    bool centerHole =
                        radius < 5.5f;

                    if (body &&
                        !centerHole)
                    {
                        pixels[
                            y * size +
                            x] =
                            new Color32(
                                255,
                                255,
                                255,
                                255);
                    }
                    else
                    {
                        pixels[
                            y * size +
                            x] =
                            new Color32(
                                0,
                                0,
                                0,
                                0);
                    }
                }
            }

            texture.SetPixels32(
                pixels);

            texture.Apply(
                false,
                true);

            Sprite sprite =
                Sprite.Create(
                    texture,
                    new Rect(
                        0f,
                        0f,
                        size,
                        size),
                    new Vector2(
                        0.5f,
                        0.5f),
                    size);

            sprite.name =
                "HKMP.List.Gear";

            return sprite;
        }

        public static void SetFullStretch(
            RectTransform rect,
            float left,
            float bottom,
            float right,
            float top)
        {
            rect.anchorMin =
                Vector2.zero;

            rect.anchorMax =
                Vector2.one;

            rect.offsetMin =
                new Vector2(
                    left,
                    bottom);

            rect.offsetMax =
                new Vector2(
                    -right,
                    -top);
        }

        public static void SetCentered(
            RectTransform rect,
            Vector2 size,
            Vector2 position)
        {
            rect.anchorMin =
                new Vector2(
                    0.5f,
                    0.5f);

            rect.anchorMax =
                new Vector2(
                    0.5f,
                    0.5f);

            rect.pivot =
                new Vector2(
                    0.5f,
                    0.5f);

            rect.sizeDelta =
                size;

            rect.anchoredPosition =
                position;
        }

        public static LayoutElement AddLayoutElement(
            GameObject gameObject,
            float width,
            float height)
        {
            LayoutElement layout =
                gameObject.AddComponent<
                    LayoutElement>();

            layout.preferredWidth =
                width;

            layout.preferredHeight =
                height;

            layout.minWidth =
                width;

            layout.minHeight =
                height;

            return layout;
        }

        public static string GetButtonKeyText(
            KeyCode keyCode)
        {
            return keyCode == KeyCode.None
                ? "None"
                : keyCode.ToString();
        }
    }
}
