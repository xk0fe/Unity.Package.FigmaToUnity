using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

#pragma warning disable S1144 // Unused private types or members should be removed

namespace Figma
{
    using Core;
    using Core.Assets;
    using Core.Uss;
    using Core.Uxml;
    using Internals;
    using static Internals.Const;
    using static Internals.PathExtensions;

    internal sealed class FigmaWriter
    {
        #region Fields
        readonly string directory;
        readonly string fileName;
        readonly string ussPath;
        readonly Data data;
        readonly NodeMetadata nodeMetadata;
        readonly AssetsInfo assetsInfo;
        readonly StylesPreprocessor stylesPreprocessor;
        #endregion

        #region Properties
        DocumentNode Document => data.document;
        #endregion

        #region Constructors
        internal FigmaWriter(string directory, string fileName, Data data, StylesPreprocessor stylesPreprocessor, NodeMetadata nodeMetadata, AssetsInfo assetsInfo)
        {
            this.directory = directory;
            this.fileName = fileName;
            this.data = data;
            this.nodeMetadata = nodeMetadata;
            this.assetsInfo = assetsInfo;
            this.stylesPreprocessor = stylesPreprocessor;

            ussPath = CombinePath(directory, $"{fileName}.{KnownFormats.uss}");
        }
        #endregion

        #region Methods
        internal async Task WriteAsync(bool overrideGlobal = false)
        {
            RootNodes rootNodes = new(data, nodeMetadata);
            // We do need this, since the Data.componentSets do not contain updated names.
            Dictionary<string, ComponentSetNode> componentSets = rootNodes.ComponentSets
                                                                          .OrderBy(x => x.id) // Ordering to avoid index confusion, since order in the Collection could vary from one request to another.
                                                                          .ToArray()
                                                                          .IndexRedundantNames(x => x.name,
                                                                                               (componentSet, postfix) => componentSet.name += postfix,
                                                                                               index => index == 0 ? string.Empty : "-" + index)
                                                                          .ToDictionary(x => x.id);

            KeyValuePair<IBaseNodeMixin, UssStyle>[] nodeStyleFiltered = stylesPreprocessor.NodeStyleMap.Where(x => x.Key.IsVisible() && (nodeMetadata.EnabledInHierarchy(x.Key) || x.Key is ComponentSetNode)).ToArray();
            UssStyle[] nodeStyleStatelessFiltered = nodeStyleFiltered.Select(x => x.Value).ToArray();
            UssStyle[] globalStaticStyles = stylesPreprocessor.Styles.Select(x => x.style).Where(x => nodeStyleStatelessFiltered.Any(y => y.DoesInherit(x))).ToArray();

            if (overrideGlobal)
            {
                await using UssWriter globalUssWriter = new(directory, ussPath);
                globalUssWriter.Write(UssStyle.overrideClass);
                globalUssWriter.Write(UssStyle.viewportClass);
                globalUssWriter.Write(globalStaticStyles.ToArray().IndexRedundantNames(x => x.Name, (style, postfix) => style.Name += postfix, index => "-" + (index + 1).NumberToWords()));
            }

            // Writing UXML files
            UxmlBuilder uxmlBuilder = new(data, nodeMetadata, ussPath, stylesPreprocessor);
            Dictionary<string, IReadOnlyList<string>> framesPaths = new(rootNodes.Frames.Count);

            foreach (CanvasNode canvasNode in rootNodes.Canvases)
            {
                framesPaths.TryAdd(canvasNode.name, new List<string>());
                // also register section names so section-parented frames can add their paths
                foreach (SectionNode section in canvasNode.children.OfType<SectionNode>())
                    framesPaths.TryAdd(section.name, new List<string>());
            }

            // write sequentially to avoid file sharing violations when multiple frames produce the same filename
            foreach (var x in rootNodes.Frames) WriteFrame(uxmlBuilder, framesPaths, componentSets, x);
            foreach (var x in rootNodes.ComponentSets) WriteComponentSet(uxmlBuilder, componentSets, x);
            foreach (var x in rootNodes.Elements) WriteTemplate(uxmlBuilder, x);

            await Task.CompletedTask;

            // Creating main UXML document
            if (overrideGlobal)
                uxmlBuilder.CreateDocument(directory, fileName, data.document, framesPaths);
        }
        #endregion

        #region Support Methods
        void WriteFrame(UxmlBuilder uxmlBuilder, Dictionary<string, IReadOnlyList<string>> framesPaths, Dictionary<string, ComponentSetNode> componentSets, DefaultFrameNode frameNode)
        {
            Dictionary<string, string> templates = new();

            void FindTemplates(BaseNode root)
            {
                Stack<BaseNode> nodes = new();
                nodes.Push(root);

                for (int depth = 0; depth < Const.maximumAllowedDepthLimit; depth++)
                {
                    if (nodes.Count == 0)
                        return;

                    BaseNode node = nodes.Pop();

                    if (!node.IsVisible() || !nodeMetadata.EnabledInHierarchy(node))
                        continue;

                    if (node is InstanceNode instanceNode)
                    {
                        if (!data.components.TryGetValue(instanceNode.componentId, out Component component))
                            continue;

                        if (component == null || component.remote || string.IsNullOrEmpty(component.componentSetId))
                            continue;

                        if (!data.componentSets.TryGetValue(component.componentSetId, out Component componentSet))
                            continue;

                        if (componentSet == null || componentSet.remote)
                            continue;

                        if (!componentSets.TryGetValue(component.componentSetId, out ComponentSetNode componentSetNode))
                            continue;

                        string template = componentSetNode.name;
                        templates[template] = CombinePath(directory, componentsDirectoryName, $"{template}.{KnownFormats.uxml}");
                    }
                    else if (nodeMetadata.GetTemplate(node) is (_, { } template) && template.NotNullOrEmpty())
                        templates[template] = CombinePath(directory, elementsDirectoryName, $"{template}.{KnownFormats.uxml}");

                    if (node is DefaultFrameNode frameNode)
                        foreach (SceneNode child in frameNode.children)
                            nodes.Push(child);
                }

                throw new System.InvalidOperationException(Const.maximumDepthLimitReachedExceptionMessage);
            }

            string rootDirectory = CombinePath(directory, framesDirectoryName, frameNode.parent.name);

            if (!Directory.Exists(rootDirectory))
                Directory.CreateDirectory(rootDirectory);

            // strip leading dot and replace slashes so Figma names don't create hidden files or subdirectories on unix
            string frameName = frameNode.name.TrimStart('.').Replace('/', '-');
            using UssWriter ussWriter = new(directory, CombinePath(rootDirectory, $"{frameName}.{KnownFormats.uss}"));
            ussWriter.Write(stylesPreprocessor.GetStyles(frameNode).IndexRedundantNames(x => x.Name, (style, postfix) => style.Name += postfix, index => "-" + (index + 1).NumberToWords()));

            FindTemplates(frameNode);

            string uxmlPath = uxmlBuilder.CreateFrame(rootDirectory, new[] { ussPath, ussWriter.Path }, templates, frameNode, frameName);
            if (framesPaths.TryGetValue(frameNode.parent.name, out IReadOnlyList<string> pathList))
                pathList.As<List<string>>().Add(uxmlPath);

            assetsInfo.AddModifiedFiles(uxmlPath, ussWriter.Path);
            templates.Clear();
        }
        void WriteComponentSet(UxmlBuilder uxmlBuilder, Dictionary<string, ComponentSetNode> componentSets, ComponentSetNode componentSet)
        {
            Dictionary<string, string> templates = new();

            void FindTemplates(BaseNode root)
            {
                Stack<BaseNode> nodes = new();
                nodes.Push(root);

                for (int depth = 0; depth < Const.maximumAllowedDepthLimit; depth++)
                {
                    if (nodes.Count == 0)
                        return;

                    BaseNode node = nodes.Pop();

                    if (!node.IsVisible() || !nodeMetadata.EnabledInHierarchy(node))
                        continue;

                    if (node is InstanceNode instanceNode)
                    {
                        if (!data.components.TryGetValue(instanceNode.componentId, out Component component))
                            continue;

                        if (component == null || component.remote || string.IsNullOrEmpty(component.componentSetId))
                            continue;

                        if (!data.componentSets.TryGetValue(component.componentSetId, out Component componentSetMeta))
                            continue;

                        if (componentSetMeta == null || componentSetMeta.remote)
                            continue;

                        if (!componentSets.TryGetValue(component.componentSetId, out ComponentSetNode componentSetNode))
                            continue;

                        string template = componentSetNode.name;
                        templates[template] = CombinePath(directory, componentsDirectoryName, $"{template}.{KnownFormats.uxml}");
                    }

                    if (node is DefaultFrameNode frameNode)
                        foreach (SceneNode child in frameNode.children)
                            nodes.Push(child);
                }

                throw new System.InvalidOperationException(Const.maximumDepthLimitReachedExceptionMessage);
            }

            using UssWriter ussWriter = new(directory, CombinePath(directory, componentsDirectoryName, $"{componentSet.name}.{KnownFormats.uss}"));
            ussWriter.Write(stylesPreprocessor.GetStyles(componentSet).IndexRedundantNames(x => x.Name, (style, postfix) => style.Name += postfix, index => "-" + (index + 1).NumberToWords()));

            FindTemplates(componentSet);

            string uxmlPath = uxmlBuilder.CreateComponentSet(CombinePath(directory, componentsDirectoryName), new[] { ussPath, ussWriter.Path }, templates, componentSet);
            assetsInfo.AddModifiedFiles(uxmlPath, ussWriter.Path);
        }
        void WriteTemplate(UxmlBuilder uxmlBuilder, (DefaultShapeNode element, string template) node)
        {
            (bool isHash, string hashedTemplates) = nodeMetadata.GetTemplate(node.element);

            using UssWriter ussWriter = new(directory, CombinePath(directory, elementsDirectoryName, $"{(isHash ? hashedTemplates : node.template)}.{KnownFormats.uss}"));
            ussWriter.Write(stylesPreprocessor.GetStyles(node.element).IndexRedundantNames(x => x.Name, (style, postfix) => style.Name += postfix, index => "-" + (index + 1).NumberToWords()));

            string uxmlPath = uxmlBuilder.CreateElement(CombinePath(directory, elementsDirectoryName), new[] { ussPath, ussWriter.Path }, node.element, node.template);
            assetsInfo.AddModifiedFiles(uxmlPath, ussWriter.Path);
        }
        #endregion
    }
}