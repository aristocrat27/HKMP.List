using System;
using System.Collections.Generic;
using Modding;
using UnityEngine;
using UnityEngine.UI;

namespace HKMP.List.UI
{
    internal sealed class ListRoundsSettingsPanel
    {
        private List<IMenuMod.MenuEntry> _entries;
        private RectTransform _root;
        private RectTransform _content;
        private ScrollRect _scroll;

        public ListRoundsSettingsPanel(IReadOnlyList<IMenuMod.MenuEntry> entries)
        {
            _entries = entries == null
                ? new List<IMenuMod.MenuEntry>()
                : new List<IMenuMod.MenuEntry>(entries);
        }

        public void SetEntries(IReadOnlyList<IMenuMod.MenuEntry> entries)
        {
            _entries = entries == null
                ? new List<IMenuMod.MenuEntry>()
                : new List<IMenuMod.MenuEntry>(entries);

            if (IsVisible)
            {
                Refresh();
            }
        }

        public bool IsVisible
        {
            get { return _root != null && _root.gameObject.activeInHierarchy; }
        }

        public void Show(Transform parent)
        {
            EnsureCreated(parent);
            if (_root != null)
            {
                _root.gameObject.SetActive(true);
                _root.SetAsLastSibling();
            }
            Refresh();
        }

        public void Hide()
        {
            if (_root != null)
                _root.gameObject.SetActive(false);
        }

        private void EnsureCreated(Transform parent)
        {
            if (_root != null)
                return;

            GameObject rootObject = ListUiFactory.CreateObject(parent, "RoundsSettingsPanel");
            _root = rootObject.GetComponent<RectTransform>();
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = new Vector2(8f, 8f);
            _root.offsetMax = new Vector2(-8f, -48f);

            Image background = rootObject.AddComponent<Image>();
            background.sprite = ListUiFactory.WhiteSprite;
            background.color = new Color(0.045f, 0.045f, 0.058f, 1f);
            background.raycastTarget = true;

            Text heading = ListUiFactory.CreateText(
                _root, "RoundsHeading", "ROUNDS", 21,
                new Color(0.83f, 0.83f, 0.90f, 1f));
            RectTransform headingRect = heading.rectTransform;
            headingRect.anchorMin = new Vector2(0f, 1f);
            headingRect.anchorMax = new Vector2(0f, 1f);
            headingRect.pivot = new Vector2(0f, 1f);
            headingRect.sizeDelta = new Vector2(220f, 32f);
            headingRect.anchoredPosition = new Vector2(10f, -4f);

            GameObject scrollObject = ListUiFactory.CreateObject(_root, "RoundsScroll");
            RectTransform viewport = scrollObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(2f, 4f);
            viewport.offsetMax = new Vector2(-2f, -38f);
            Image viewportImage = scrollObject.AddComponent<Image>();
            viewportImage.sprite = ListUiFactory.WhiteSprite;
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = true;
            scrollObject.AddComponent<RectMask2D>();

            _scroll = scrollObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;
            _scroll.viewport = viewport;

            GameObject contentObject = ListUiFactory.CreateObject(viewport, "RoundsContent");
            _content = contentObject.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 7f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _content;
            _root.gameObject.SetActive(false);
        }

        public void Refresh()
        {
            if (_content == null)
                return;

            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                Transform child = _content.GetChild(i);
                if (child == null)
                    continue;

                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }

            int renderedEntries = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                IMenuMod.MenuEntry entry = _entries[i];
                if (entry.Equals(default(IMenuMod.MenuEntry))) continue;

                CreateEntryRow(i, entry);
                renderedEntries++;
            }

            if (renderedEntries == 0)
            {
                Text empty = ListUiFactory.CreateText(
                    _content, "RoundsUnavailable", "No HKMP.Rounds settings are available.", 13,
                    new Color(0.70f, 0.70f, 0.74f, 1f));
                ListUiFactory.AddLayoutElement(empty.gameObject, 0f, 36f);
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            if (_scroll != null)
                _scroll.verticalNormalizedPosition = 1f;
        }

        private void CreateEntryRow(int index, IMenuMod.MenuEntry entry)
        {
            GameObject row = ListUiFactory.CreateObject(_content, "RoundsSetting_" + index);
            ListUiFactory.AddLayoutElement(row, 0f, 72f);
            HorizontalLayoutGroup horizontal = row.AddComponent<HorizontalLayoutGroup>();
            horizontal.spacing = 8f;
            horizontal.childAlignment = TextAnchor.MiddleLeft;
            horizontal.childControlWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childForceExpandWidth = false;
            horizontal.childForceExpandHeight = true;

            GameObject labels = ListUiFactory.CreateObject(row.transform, "Labels");
            ListUiFactory.AddLayoutElement(labels, 0f, 66f);
            LayoutElement labelsLayout = labels.GetComponent<LayoutElement>();
            if (labelsLayout != null)
                labelsLayout.flexibleWidth = 1f;

            VerticalLayoutGroup vertical = labels.AddComponent<VerticalLayoutGroup>();
            vertical.spacing = 1f;
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;

            Text name = ListUiFactory.CreateText(
                labels.transform, "Name", entry.Name ?? "Setting", 14, Color.white);
            ListUiFactory.AddLayoutElement(name.gameObject, 0f, 25f);

            Text description = ListUiFactory.CreateText(
                labels.transform, "Description", entry.Description ?? string.Empty, 12,
                new Color(0.68f, 0.68f, 0.73f, 1f));
            ListUiFactory.AddLayoutElement(description.gameObject, 0f, 38f);

            string[] values = entry.Values ?? new string[0];
            int currentIndex = SafeLoad(entry, values.Length);
            string caption = values.Length == 0
                ? "Unavailable"
                : values[currentIndex];

            Button valueButton = ListUiFactory.CreateButton(
                row.transform, "Value", caption, delegate
                {
                    if (values.Length == 0 || entry.Saver == null)
                        return;

                    int next = (SafeLoad(entry, values.Length) + 1) % values.Length;
                    try
                    {
                        entry.Saver(next);
                        Refresh();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("HKMP.List: failed to change HKMP.Rounds setting '" +
                            (entry.Name ?? "Setting") + "': " + exception);
                    }
                }, 12);
            ListUiFactory.AddLayoutElement(valueButton.gameObject, 116f, 36f);
            valueButton.interactable = values.Length > 0 && entry.Saver != null;
        }

        private static int SafeLoad(IMenuMod.MenuEntry entry, int valueCount)
        {
            if (valueCount <= 0 || entry.Loader == null)
                return 0;

            try
            {
                return Mathf.Clamp(entry.Loader(), 0, valueCount - 1);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("HKMP.List: failed to read HKMP.Rounds setting '" +
                    (entry.Name ?? "Setting") + "': " + exception.Message);
                return 0;
            }
        }
    }
}
