// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Text;
using FlaxEngine;

namespace FlaxEditor.Content
{
    /// <summary>
    /// Content item for an asset that lives in memory and has no file: one created at run time with
    /// <see cref="FlaxEngine.Content.CreateVirtualAsset{T}()"/> and filled in by code.
    /// <para>
    /// It exists so that such an asset can be opened in the ordinary editor window for its type. The
    /// window machinery is built around a content item, and everything it needs from one - the id, the
    /// type and a name to show - a virtual asset has; only the file does not exist. Anything that
    /// would touch that file (renaming, deleting, reimporting, a thumbnail) is off, and the windows
    /// that clone an asset to edit it open the live one read-only instead.
    /// </para>
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.BinaryAssetItem" />
    public class VirtualAssetItem : BinaryAssetItem
    {
        private readonly Asset _asset;

        /// <summary>
        /// The live asset this item stands for.
        /// </summary>
        public Asset Asset => _asset;

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualAssetItem"/> class.
        /// </summary>
        /// <param name="asset">The live virtual asset.</param>
        /// <param name="displayPath">
        /// A path to show in the window title and tooltips. It does not have to exist: for an asset
        /// built from some outside source, the natural value is that source's own path.
        /// </param>
        /// <param name="searchFilter">The search filter type.</param>
        public VirtualAssetItem(Asset asset, string displayPath, ContentItemSearchFilter searchFilter = ContentItemSearchFilter.Other)
        : this(asset ?? throw new ArgumentNullException(nameof(asset)),
               displayPath ?? throw new ArgumentNullException(nameof(displayPath)),
               asset.ID, searchFilter)
        {
        }

        private VirtualAssetItem(Asset asset, string displayPath, Guid id, ContentItemSearchFilter searchFilter)
        : base(displayPath, ref id, asset.GetType().FullName, asset.GetType(), searchFilter)
        {
            _asset = asset;
        }

        /// <inheritdoc />
        public override bool Exists => false;

        /// <inheritdoc />
        public override bool CanRename => false;

        /// <inheritdoc />
        public override void RefreshThumbnail()
        {
            // No file, so nothing to draw a thumbnail from and nowhere to cache one.
        }

        /// <inheritdoc />
        protected override void OnBuildTooltipText(StringBuilder sb)
        {
            sb.Append("Virtual asset (no file)").AppendLine();
            sb.Append("Source: ").Append(Path).AppendLine();
            sb.Append("Type: ").Append(TypeName).AppendLine();
            sb.Append("ID: ").Append(ID).AppendLine();
        }
    }
}
