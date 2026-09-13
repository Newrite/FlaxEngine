// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Globalization;
using System.Reflection;
using System.Xml;
using FlaxEditor.Content;
using FlaxEditor.Content.Import;
using FlaxEditor.CustomEditors;
using FlaxEditor.CustomEditors.Editors;
using FlaxEditor.GUI;
using FlaxEditor.GUI.Timeline;
using FlaxEditor.Scripting;
using FlaxEditor.Viewport.Cameras;
using FlaxEditor.Viewport.Previews;
using FlaxEngine;
using FlaxEngine.GUI;
using Object = FlaxEngine.Object;

namespace FlaxEditor.Windows.Assets
{
    /// <summary>
    /// Editor window to view/modify <see cref="Animation"/> asset.
    /// </summary>
    /// <seealso cref="Animation" />
    /// <seealso cref="FlaxEditor.Windows.Assets.AssetEditorWindow" />
    public sealed class AnimationWindow : ClonedAssetEditorWindowBase<Animation>
    {
        private sealed class Preview : AnimationPreview
        {
            private readonly AnimationWindow _window;
            private AnimationGraph _animGraph;

            public SkinnedModel BaseModel;

            /// <summary>
            /// Whether the clip's root motion moves the previewed character. With it on, a clip that
            /// travels - a run, a dodge - walks the character out of the viewport and every replay
            /// starts further away; with it off the same clip plays on the spot.
            /// </summary>
            public bool RootMotion = true;

            public Preview(AnimationWindow window)
            : base(true)
            {
                _window = window;
                ShowFloor = true;
            }

            public void SetModel(SkinnedModel model)
            {
                PreviewActor.SkinnedModel = model;
                PreviewActor.AnimationGraph = null;
                PreviewActor.LocalPosition = Vector3.Zero;
                PreviewActor.LocalOrientation = Quaternion.Identity;
                Object.Destroy(ref _animGraph);
                if (!model)
                    return;
                var baseModel = BaseModel ?? model;

                // Use virtual animation graph to playback the animation
                _animGraph = FlaxEngine.Content.CreateVirtualAsset<AnimationGraph>();
                _animGraph.InitAsAnimation(baseModel, _window.Asset, true, RootMotion);
                PreviewActor.AnimationGraph = _animGraph;
            }

            /// <inheritdoc />
            public override void Draw()
            {
                base.Draw();

                var style = Style.Current;
                var animation = _window.Asset;
                if (animation == null || !animation.IsLoaded)
                {
                    Render2D.DrawText(style.FontLarge, "Loading...", new Rectangle(Float2.Zero, Size), style.ForegroundDisabled, TextAlignment.Center, TextAlignment.Center);
                }
            }

            /// <inheritdoc />
            public override void OnDestroy()
            {
                BaseModel = null;
                Object.Destroy(ref _animGraph);

                base.OnDestroy();
            }
        }

        [CustomEditor(typeof(ProxyEditor))]
        private sealed class PropertiesProxy
        {
            private AnimationWindow Window;
            private Animation Asset;

            /// <summary>True once OnLoad has linked this proxy to its window; before that it ignores
            /// anything set on it, so a value has to wait for the asset to load.</summary>
            public bool IsLinked => Window != null;
            private ModelImportSettings ImportSettings = new ModelImportSettings();
            private bool EnablePreviewModelCache = true;

            [EditorDisplay("Preview"), NoSerialize, AssetReference(true), Tooltip("The skinned model to preview the animation playback.")]
            public SkinnedModel PreviewModel
            {
                get => Window?._preview?.SkinnedModel;
                set
                {
                    if (Window == null || PreviewModel == value)
                        return;
                    if (Window._preview == null)
                    {
                        // Animation preview
                        Window._preview = new Preview(Window)
                        {
                            RootMotion = Window._initialRootMotion,
                            ViewportCamera = new FPSCamera(),
                            ScaleToFit = false,
                            AnchorPreset = AnchorPresets.StretchAll,
                            Offsets = Margin.Zero,
                        };
                    }

                    Window._preview.SetModel(value);
                    Window._timeline.Preview = value ? Window._preview : null;

                    if (Window._panel2 == null)
                    {
                        // Properties panel
                        Window._panel2 = new SplitPanel(Orientation.Vertical, ScrollBars.None, ScrollBars.Vertical)
                        {
                            AnchorPreset = AnchorPresets.StretchAll,
                            Offsets = Margin.Zero,
                            SplitterValue = 0.6f,
                        };
                        Window._preview.Parent = Window._panel2.Panel1;
                    }

                    // Show panel2 with preview and properties or just properties inside panel2 2nd part
                    if (value)
                    {
                        Window._panel2.Parent = Window._panel1.Panel2;
                        Window._propertiesPresenter.Panel.Parent = Window._panel2.Panel2;
                    }
                    else
                    {
                        Window._panel2.Parent = null;
                        Window._propertiesPresenter.Panel.Parent = Window._panel1.Panel2;
                    }

                    if (value)
                    {
                        // Focus model
                        value.WaitForLoaded(500);
                        Window._preview.ViewportCamera.SetArcBallView(Window._preview.PreviewActor.Sphere);
                    }

                    if (EnablePreviewModelCache)
                    {
                        var customDataName = Window.GetPreviewModelCacheName();
                        if (value)
                            Window.Editor.ProjectCache.SetCustomData(customDataName, value.ID.ToString());
                        else
                            Window.Editor.ProjectCache.RemoveCustomData(customDataName);
                    }
                }
            }

            [EditorDisplay("Preview"), NoSerialize, VisibleIf(nameof(ShowBaseModel))]
            [Tooltip("Let the clip's root motion move the character. Off plays it on the spot, which is what a clip that travels needs if it is to stay in view.")]
            public bool RootMotion
            {
                get => Window?._preview?.RootMotion ?? true;
                set
                {
                    if (Window?._preview == null || Window._preview.RootMotion == value)
                        return;
                    Window._preview.RootMotion = value;
                    Window._preview.SetModel(PreviewModel);
                }
            }

            private bool ShowBaseModel => PreviewModel != null;

            [EditorDisplay("Preview"), NoSerialize, AssetReference(true), VisibleIf(nameof(ShowBaseModel))]
            [Tooltip("The skinned model to use as a retarget source. Animation will be played using its skeleton and retarget into the Preview Model.")]
            public SkinnedModel BaseModel
            {
                get => Window?._preview?.BaseModel;
                set
                {
                    if (Window == null || PreviewModel == value)
                        return;

                    // Reinit
                    Window._preview.BaseModel = value;
                    Window._preview.SetModel(PreviewModel);
                }
            }

            public void OnLoad(AnimationWindow window)
            {
                // Link
                Window = window;
                Asset = window.Asset;
                EnablePreviewModelCache = true;

                // Try to restore target asset import options (useful for fast reimport)
                if (!(window.Item is VirtualAssetItem))
                    Editor.TryRestoreImportOptions(ref ImportSettings.Settings, window.Item.Path);
            }

            public void OnClean()
            {
                // Unlink
                EnablePreviewModelCache = false;
                PreviewModel = null;
                Window = null;
                Asset = null;
            }

            public void Reimport()
            {
                Editor.Instance.ContentImporting.Reimport((BinaryAssetItem)Window.Item, ImportSettings, true);
            }

            private class ProxyEditor : GenericEditor
            {
                /// <inheritdoc />
                public override void Initialize(LayoutElementsContainer layout)
                {
                    var proxy = (PropertiesProxy)Values[0];
                    if (Utilities.Utils.OnAssetProperties(layout, proxy.Asset))
                        return;

                    // General properties
                    {
                        var group = layout.Group("General");

                        var info = proxy.Asset.Info;
                        group.Label("Length: " + info.Length + "s");
                        group.Label("Frames: " + info.FramesCount);
                        group.Label("Channels: " + info.ChannelsCount);
                        group.Label("Keyframes: " + info.KeyframesCount);
                        group.Label("Memory Usage: " + Utilities.Utils.FormatBytesCount((ulong)info.MemoryUsage));
                    }

                    base.Initialize(layout);

                    // Ignore import settings GUI if the type is not animation. This removes the import UI if the animation asset was not created using an import.
                    if (proxy.ImportSettings.Settings.Type != FlaxEngine.Tools.ModelTool.ModelType.Animation)
                        return;

                    // Import Settings
                    {
                        var group = layout.Group("Import Settings");

                        var importSettingsField = typeof(PropertiesProxy).GetField("ImportSettings", BindingFlags.NonPublic | BindingFlags.Instance);
                        var importSettingsValues = new ValueContainer(new ScriptMemberInfo(importSettingsField)) { proxy.ImportSettings };
                        group.Object(importSettingsValues);

                        // Creates the import path UI
                        group = layout.Group("Import Path");
                        Utilities.Utils.CreateImportPathUI(group, proxy.Window.Item as BinaryAssetItem);

                        layout.Space(5);
                        var reimportButton = layout.Button("Reimport");
                        reimportButton.Button.Clicked += () => ((PropertiesProxy)Values[0]).Reimport();
                    }
                }
            }
        }

        private CustomEditorPresenter _propertiesPresenter;
        private PropertiesProxy _properties;
        private SplitPanel _panel1;
        private SplitPanel _panel2;
        private Preview _preview;
        private AnimationTimeline _timeline;
        private Undo _undo;
        private ToolStripButton _saveButton;
        private ToolStripButton _undoButton;
        private ToolStripButton _redoButton;
        private bool _isWaitingForTimelineLoad;
        private SkinnedModel _initialPreviewModel, _initialBaseModel;
        private bool _initialRootMotion = true;
        private float _initialPanel2Splitter = 0.6f;
        private bool _timelineIsDirty;

        /// <summary>
        /// Gets the animation timeline editor.
        /// </summary>
        public AnimationTimeline Timeline => _timeline;

        /// <summary>
        /// True when this window shows an asset that has no file - a virtual one. Such a window is a
        /// viewer: it cannot save, and its timeline cannot be edited.
        /// </summary>
        public bool IsVirtualAsset => _item is VirtualAssetItem;

        /// <summary>
        /// Gets or sets the skinned model the animation is previewed on. The same thing the Preview
        /// Model field in the properties panel sets, reachable from code so that a tool can open a
        /// window already showing the right character.
        /// </summary>
        public SkinnedModel PreviewModel
        {
            get => _properties?.PreviewModel;
            set
            {
                // Before the asset is loaded the properties proxy is not linked to this window yet and
                // ignores what it is given, so the value waits for OnAssetLoaded like the cached one.
                if (_properties != null && _properties.IsLinked)
                    _properties.PreviewModel = value;
                else
                    _initialPreviewModel = value;
            }
        }

        /// <summary>
        /// Whether the previewed character is moved by the clip's root motion. On by default, which is
        /// what an animation authored against a scene expects; off keeps a travelling clip in view.
        /// </summary>
        public bool PreviewRootMotion
        {
            get => _preview?.RootMotion ?? _initialRootMotion;
            set
            {
                _initialRootMotion = value;
                if (_preview == null)
                    return;
                if (_preview.RootMotion == value)
                    return;
                _preview.RootMotion = value;
                _preview.SetModel(PreviewModel);
            }
        }

        /// <summary>
        /// Gets or sets the skinned model the animation was authored against, used as the retarget
        /// source when it differs from <see cref="PreviewModel"/>.
        /// </summary>
        public SkinnedModel PreviewBaseModel
        {
            get => _preview?.BaseModel;
            set
            {
                if (_properties != null && _properties.IsLinked && _preview != null)
                    _properties.BaseModel = value;
                else
                    _initialBaseModel = value;
            }
        }

        /// <summary>
        /// Writes this animation to a file, which can then be opened as an ordinary asset. Meant for a
        /// virtual asset, which has nothing on disk: <see cref="Asset.Save"/> takes a path and accepts
        /// a virtual asset when it is given one.
        /// </summary>
        /// <param name="path">The destination path.</param>
        /// <returns>True if failed, otherwise false.</returns>
        public bool Export(string path)
        {
            if (_asset == null || string.IsNullOrEmpty(path))
                return true;
            if (_asset.WaitForLoaded())
                return true;
            if (_asset.Save(path))
                return true;
            // Let the content database see the new file. Find() returns null for a folder it has not
            // indexed yet, and RefreshFolder(null) throws, so the lookup is checked.
            var folder = Editor.ContentDatabase.Find(System.IO.Path.GetDirectoryName(path));
            if (folder != null)
                Editor.ContentDatabase.RefreshFolder(folder, true);
            return false;
        }

        /// <summary>
        /// Gets the undo history context for this window.
        /// </summary>
        public Undo Undo => _undo;

        /// <inheritdoc />
        public AnimationWindow(Editor editor, AssetItem item)
        : base(editor, item)
        {
            var inputOptions = Editor.Options.Options.Input;

            // Undo
            _undo = new Undo();
            _undo.UndoDone += OnUndoRedo;
            _undo.RedoDone += OnUndoRedo;
            _undo.ActionDone += OnUndoRedo;

            // Main panel
            _panel1 = new SplitPanel(Orientation.Horizontal, ScrollBars.None, ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                SplitterValue = 0.8f,
                Offsets = new Margin(0, 0, _toolstrip.Bottom, 0),
                Parent = this
            };

            // Timeline
            _timeline = new AnimationTimeline(_undo)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = Margin.Zero,
                Parent = _panel1.Panel1,
                Enabled = false
            };
            _timeline.Modified += OnTimelineModified;
            _timeline.SetNoTracksText("Loading...");

            // Asset properties
            _propertiesPresenter = new CustomEditorPresenter(null);
            _propertiesPresenter.Panel.Parent = _panel1.Panel2;
            _properties = new PropertiesProxy();
            _propertiesPresenter.Select(_properties);

            // Toolstrip
            _saveButton = _toolstrip.AddButton(Editor.Icons.Save64, Save).LinkTooltip("Save", ref inputOptions.Save);
            if (item is VirtualAssetItem)
            {
                // Nothing to save into: the asset has no file. Offer to make one instead.
                _saveButton.Enabled = false;
                _saveButton.TooltipText = "This animation is virtual and has no file to save to";
                _toolstrip.AddButton(Editor.Icons.AddFile64, OnExportClicked).LinkTooltip("Export to a file in the project");
                _timeline.CanEdit = false;
            }
            _toolstrip.AddSeparator();
            _undoButton = _toolstrip.AddButton(Editor.Icons.Undo64, _undo.PerformUndo).LinkTooltip("Undo", ref inputOptions.Undo);
            _redoButton = _toolstrip.AddButton(Editor.Icons.Redo64, _undo.PerformRedo).LinkTooltip("Redo", ref inputOptions.Redo);
            _toolstrip.AddSeparator();
            _toolstrip.AddButton(editor.Icons.Docs64, () => Platform.OpenUrl(Utilities.Constants.DocsUrl + "manual/animation/animation/index.html")).LinkTooltip("See documentation to learn more");

            // Setup input actions
            InputActions.Add(options => options.Undo, _undo.PerformUndo);
            InputActions.Add(options => options.Redo, _undo.PerformRedo);
        }

        private void OnExportClicked()
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(_item.ShortName);
            if (string.IsNullOrEmpty(name))
                name = "Animation";
            var suggested = System.IO.Path.Combine(Globals.ProjectContentFolder, name + ".flax");
            if (FileSystem.ShowSaveFileDialog(Editor.Windows.MainWindow, Globals.ProjectContentFolder,
                                              "Flax Asset (*.flax)\0*.flax\0", false, "Export animation",
                                              out var files) || files == null || files.Length == 0)
            {
                // The dialog was cancelled, or the platform has none: fall back to the suggested path.
                if (files == null || files.Length == 0)
                    return;
            }
            var path = files.Length > 0 ? files[0] : suggested;
            if (Export(path))
                Editor.LogError("Failed to export the animation to " + path);
            else
                Editor.Log("Exported the animation to " + path);
        }

        private void OnUndoRedo(IUndoAction action)
        {
            MarkAsEdited();
            UpdateToolstrip();
            _propertiesPresenter.BuildLayout();
        }

        private void OnTimelineModified()
        {
            _timelineIsDirty = true;
            MarkAsEdited();
        }

        private bool RefreshTempAsset()
        {
            if (_asset == null || _isWaitingForTimelineLoad)
                return true;
            if (_timeline.IsModified)
            {
                _timeline.Save(_asset);
            }
            _propertiesPresenter.BuildLayoutOnUpdate();

            return false;
        }

        private string GetPreviewModelCacheName()
        {
            return _item.ID + ".PreviewModel";
        }

        /// <inheritdoc />
        protected override void OnAssetLoaded()
        {
            _properties.OnLoad(this);
            _propertiesPresenter.BuildLayout();
            ClearEditedFlag();
            if (!_initialPreviewModel && 
                Editor.ProjectCache.TryGetCustomData(GetPreviewModelCacheName(), out string str) && 
                Guid.TryParse(str, out var id))
            {
                _initialPreviewModel = FlaxEngine.Content.LoadAsync<SkinnedModel>(id);
            }
            if (_initialPreviewModel)
            {
                _properties.PreviewModel = _initialPreviewModel;
                _panel2.SplitterValue = _initialPanel2Splitter;
                _initialPreviewModel = null;
                if (_initialBaseModel)
                {
                    _properties.BaseModel = _initialBaseModel;
                }
            }
            _initialBaseModel = null;

            base.OnAssetLoaded();
        }

        /// <inheritdoc />
        public override void Save()
        {
            if (IsVirtualAsset)
            {
                // SaveToOriginal copies the cloned file over the original, and there is neither.
                Editor.LogWarning("Cannot save a virtual animation: it has no file. Use Export instead.");
                return;
            }
            if (!IsEdited)
                return;

            if (RefreshTempAsset())
                return;
            if (SaveToOriginal())
                return;

            ClearEditedFlag();
            _item.RefreshThumbnail();
        }

        /// <inheritdoc />
        protected override void UpdateToolstrip()
        {
            _saveButton.Enabled = IsEdited && !IsVirtualAsset;
            _undoButton.Enabled = _undo.CanUndo;
            _redoButton.Enabled = _undo.CanRedo;

            base.UpdateToolstrip();
        }

        /// <inheritdoc />
        protected override void UnlinkItem()
        {
            _isWaitingForTimelineLoad = false;
            _properties.OnClean();
            _timeline.Preview = null;

            base.UnlinkItem();
        }

        /// <inheritdoc />
        protected override void OnAssetLinked()
        {
            _isWaitingForTimelineLoad = true;

            base.OnAssetLinked();
        }

        /// <inheritdoc />
        public override void OnItemReimported(ContentItem item)
        {
            // Refresh the properties (will get new data in OnAssetLoaded)
            _properties.OnClean();
            _propertiesPresenter.BuildLayout();
            ClearEditedFlag();

            // Reload timeline
            _timeline.Enabled = false;
            _isWaitingForTimelineLoad = true;

            base.OnItemReimported(item);

            // Drop virtual asset state and get a new one from the reimported file
            LoadFromOriginal();
        }

        /// <inheritdoc />
        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // Check if temporary asset need to be updated
            if (_timelineIsDirty)
            {
                _timelineIsDirty = false;
                RefreshTempAsset();
            }

            // Check if need to load timeline
            if (_isWaitingForTimelineLoad && _asset.IsLoaded)
            {
                _isWaitingForTimelineLoad = false;
                _timeline._id = _item.ID;
                _timeline.Load(_asset);
                _undo.Clear();
                _timeline.Enabled = true;
                _timeline.SetNoTracksText(null);
                ClearEditedFlag();
                _timeline.ShowWholeTimeline();
            }
        }

        /// <inheritdoc />
        public override bool UseLayoutData => true;

        /// <inheritdoc />
        public override void OnLayoutSerialize(XmlWriter writer)
        {
            LayoutSerializeSplitter(writer, "TimelineSplitter", _timeline.Splitter);
            LayoutSerializeSplitter(writer, "Panel1Splitter", _panel1);
            if (_panel2 != null)
                LayoutSerializeSplitter(writer, "Panel2Splitter", _panel2);
            writer.WriteAttributeString("TimeShowMode", _timeline.TimeShowMode.ToString());
            writer.WriteAttributeString("ShowPreviewValues", _timeline.ShowPreviewValues.ToString());
            if (_properties.PreviewModel)
                writer.WriteAttributeString("PreviewModel", _properties.PreviewModel.ID.ToString());
            if (_properties.BaseModel)
                writer.WriteAttributeString("BaseModel", _properties.BaseModel.ID.ToString());
        }

        /// <inheritdoc />
        public override void OnLayoutDeserialize(XmlElement node)
        {
            LayoutDeserializeSplitter(node, "TimelineSplitter", _timeline.Splitter);
            LayoutDeserializeSplitter(node, "Panel1Splitter", _panel1);
            if (float.TryParse(node.GetAttribute("Panel2Splitter"), CultureInfo.InvariantCulture, out float value1) && value1 > 0.01f && value1 < 0.99f)
                _initialPanel2Splitter = value1;
            if (Enum.TryParse(node.GetAttribute("TimeShowMode"), out Timeline.TimeShowModes value2))
                _timeline.TimeShowMode = value2;
            if (bool.TryParse(node.GetAttribute("ShowPreviewValues"), out bool value3))
                _timeline.ShowPreviewValues = value3;
            if (Guid.TryParse(node.GetAttribute("PreviewModel"), out Guid value4))
                _initialPreviewModel = FlaxEngine.Content.LoadAsync<SkinnedModel>(value4);
            if (Guid.TryParse(node.GetAttribute("BaseModel"), out value4))
                _initialBaseModel = FlaxEngine.Content.LoadAsync<SkinnedModel>(value4);
        }

        /// <inheritdoc />
        public override void OnLayoutDeserialize()
        {
            _timeline.Splitter.SplitterValue = 0.2f;
        }

        /// <inheritdoc />
        public override void OnDestroy()
        {
            if (IsDisposing)
                return;
            base.OnDestroy();

            if (_undo != null)
            {
                _undo.Enabled = false;
                _undo.Clear();
                _undo = null;
            }

            _preview = null;
            _timeline = null;
            _propertiesPresenter = null;
            _properties = null;
            _panel1 = null;
            _panel2 = null;
            _saveButton = null;
            _undoButton = null;
            _redoButton = null;
        }
    }
}
