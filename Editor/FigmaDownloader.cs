using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace Figma
{
    using Core;
    using Core.Assets;
    using Internals;
    using static Internals.Const;
    using static Internals.PathExtensions;

    internal class FigmaDownloader : Api
    {
        #region Consts
        const int maxConcurrentRequests = 5;
        const int maxComponentsIdsInOneRequest = 400;
        #endregion

        #region Fields
        readonly AssetsInfo assetsInfo;

        string componentsDirectoryPath;
        string elementsDirectoryPath;
        string imagesDirectoryPath;
        string framesDirectoryPath;

        FigmaWriter figmaWriter;
        NodeMetadata nodeMetadata;
        NodesRegistry nodesRegistry;
        StylesPreprocessor stylesPreprocessor;
        #endregion

        #region Constructors
        internal FigmaDownloader(string personalAccessToken, string fileKey, AssetsInfo assetsInfo) : base(personalAccessToken, fileKey) => this.assetsInfo = assetsInfo;
        #endregion

        #region Methods
        internal async Task Run(bool downloadImages, string uxmlName, IReadOnlyCollection<Type> frames, bool prune, bool filter, bool systemCopyBuffer, int progress, CancellationToken token)
        {
            Directory.CreateDirectory(framesDirectoryPath = assetsInfo.GetAbsolutePath(framesDirectoryName));
            Directory.CreateDirectory(imagesDirectoryPath = assetsInfo.GetAbsolutePath(imagesDirectoryName));
            Directory.CreateDirectory(elementsDirectoryPath = assetsInfo.GetAbsolutePath(elementsDirectoryName));
            Directory.CreateDirectory(componentsDirectoryPath = assetsInfo.GetAbsolutePath(componentsDirectoryName));

            int steps = downloadImages ? 5 : 4;

            await assetsInfo.cachedAssets.LoadAsync(token);

            Progress.Report(progress, 1, steps, "Downloading file");

            List<string> visibleSceneNodes = new(32);

            // collect node IDs declared directly on [Uxml(NodeId = "...")] — avoids the file-level discovery fetch
            IEnumerable<string> declaredNodeIds = frames
                .Select(t => t.GetCustomAttribute<Attributes.UxmlAttribute>()?.NodeId)
                .Where(id => !string.IsNullOrEmpty(id));

            Data data;

            bool pageMode = false;

            if (declaredNodeIds.Any())
            {
                // use /nodes endpoint to request only the declared subtrees — avoids fetching siblings
                string nodeIds = string.Join(",", declaredNodeIds);
                Progress.SetDescription(progress, "Resolving Figma nodes");
                string nodesJson = await GetJsonAsync($"files/{fileKey}/nodes?ids={nodeIds}", token);

                if (systemCopyBuffer)
                    GUIUtility.systemCopyBuffer = nodesJson;

                Progress.Report(progress, 2, steps, "Parsing Figma file");

                Nodes nodesResponse = await ConvertOnBackgroundAsync<Nodes>(nodesJson, token);

                static Dictionary<TK, TV> MergeDicts<TK, TV>(IEnumerable<Dictionary<TK, TV>> dicts) =>
                    dicts.Where(d => d != null)
                         .SelectMany(d => d)
                         .GroupBy(kv => kv.Key)
                         .ToDictionary(g => g.Key, g => g.First().Value);

                // detect canvas (page-level) nodes — SceneNodeConverter returns null for CANVAS type
                JObject rawJson = JObject.Parse(nodesJson);
                CanvasNode[] canvases = nodesResponse.nodes
                    .Where(kv => kv.Value?.document == null)
                    .Select(kv => rawJson["nodes"]?[kv.Key] is JObject nodeObj ? nodeObj["document"] as JObject : null)
                    .Where(doc => doc?["type"]?.Value<string>() == NodeType.CANVAS.ToString())
                    .Select(doc => JsonUtility.FromJObject<CanvasNode>(doc))
                    .Where(c => c != null)
                    .ToArray();

                if (canvases.Length > 0)
                {
                    // page-level fetch: write all frames from the canvas, no binding filter
                    pageMode = true;
                    DocumentNode syntheticDocument = new()
                    {
                        type = NodeType.DOCUMENT,
                        id = "0:0",
                        name = "Document",
                        children = canvases
                    };
                    data = new Data
                    {
                        document = syntheticDocument,
                        components = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.components)),
                        componentSets = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.componentSets)),
                        styles = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.styles)),
                    };
                }
                else
                {
                    // frame-level fetch: build synthetic canvas from fetched frames
                    string pageName = frames
                        .Select(t => t.GetCustomAttribute<Attributes.UxmlAttribute>()?.Root?.Split('/')[0])
                        .FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "Page";

                    CanvasNode syntheticCanvas = new()
                    {
                        type = NodeType.CANVAS,
                        id = "synthetic:1",
                        name = pageName,
                        children = nodesResponse.nodes.Values
                            .Where(v => v?.document != null)
                            .Select(v => v.document)
                            .ToArray()
                    };
                    DocumentNode syntheticDocument = new()
                    {
                        type = NodeType.DOCUMENT,
                        id = "0:0",
                        name = "Document",
                        children = new[] { syntheticCanvas }
                    };
                    data = new Data
                    {
                        document = syntheticDocument,
                        components = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.components)),
                        componentSets = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.componentSets)),
                        styles = MergeDicts(nodesResponse.nodes.Values.Where(v => v != null).Select(v => v.styles)),
                    };
                }
            }
            else
            {
                if (filter)
                {
                    Progress.SetDescription(progress, "Filtering nodes");

                    Data shallowData = await GetAsync<Data>($"files/{fileKey}?depth=3", token);
                    shallowData.document.SetParent();

                    NodeMetadata shallowMetadata = new(shallowData.document, frames, true, false, true);

                    // flatten page > direct-child and page > section > child so section-nested frames are reachable
                    IEnumerable<IBaseNodeMixin> candidates = shallowData.document.children
                        .SelectMany(page => page.children
                            .SelectMany(child => child is SectionNode section
                                ? section.children.Cast<IBaseNodeMixin>()
                                : new[] { child }));

                    visibleSceneNodes.AddRange(candidates.Where(shallowMetadata.EnabledInHierarchy).Select(node => node.id));

                    Progress.SetDescription(progress, string.Empty);
                }

                string idsString = visibleSceneNodes.Any() ? $"?ids={string.Join(",", visibleSceneNodes)}" : string.Empty;

                Progress.SetDescription(progress, "Resolving Figma file");
                string json = await GetJsonAsync($"files/{fileKey}{idsString}", token);

                if (systemCopyBuffer)
                    GUIUtility.systemCopyBuffer = json;

                Progress.Report(progress, 2, steps, "Parsing Figma file");

                data = await ConvertOnBackgroundAsync<Data>(json, token);
            }
            data.document.SetParent();

            Progress.SetDescription(progress, "Creating entities");
            // page mode: disable filter so all frames in the canvas are written regardless of [Uxml] bindings
            nodeMetadata = pageMode
                ? new NodeMetadata(data.document, Enumerable.Empty<Type>(), false)
                : new NodeMetadata(data.document, frames, filter);
            nodesRegistry = new NodesRegistry(data, nodeMetadata);
            stylesPreprocessor = new StylesPreprocessor(data, assetsInfo);
            figmaWriter = new FigmaWriter(assetsInfo.directory, uxmlName, data, stylesPreprocessor, nodeMetadata, assetsInfo);

            Progress.Report(progress, 3, steps, "Downloading missing components");
            await DownloadDocumentsAsync(token);

            if (downloadImages)
            {
                Progress.Report(progress, 4, steps, "Downloading images");
                Progress.SetDescription(progress, "Writing Gradients");
                await WriteGradientsAsync(token);
                Progress.SetDescription(progress, "Downloading image fills");
                await GetImageFillsAsync(progress, nodesRegistry.ImageFills, token);
                Progress.SetDescription(progress, $"Downloading {KnownFormats.png} files");
                await GetImageNodesAsync(progress, nodesRegistry.Pngs, UxmlDownloadImages.RenderAsPng, KnownFormats.png, token);
                Progress.SetDescription(progress, $"Downloading {KnownFormats.svg} files");
                await GetImageNodesAsync(progress, nodesRegistry.Svgs, UxmlDownloadImages.RenderAsSvg, KnownFormats.svg, token);
                await assetsInfo.cachedAssets.SaveAsync();
            }

            Progress.SetStepLabel(progress, string.Empty);

            Progress.Report(progress, steps, steps, "Updating *.uss/*.uxml files");
            await figmaWriter.WriteAsync(prune);
        }
        internal void CleanUp(bool cleanImages = false)
        {
            void CleanDirectory(string directory, string[] filters)
            {
                IEnumerable<string> target = filters.SelectMany(filter => GetFiles(directory, filter, SearchOption.AllDirectories))
                                                    .Where(fileName => !assetsInfo.modifiedContent.Contains(fileName));

                foreach (string file in target)
                {
                    FileUtil.DeleteFileOrDirectory(file);

                    string meta = $"{file}.{KnownFormats.meta}";

                    if (File.Exists(meta))
                        FileUtil.DeleteFileOrDirectory(meta);
                }
            }

            string[] textExtensions = { $"*.{KnownFormats.uxml}", $"*.{KnownFormats.uss}" };
            CleanDirectory(componentsDirectoryPath, textExtensions);
            CleanDirectory(elementsDirectoryPath, textExtensions);
            CleanDirectory(framesDirectoryPath, textExtensions);

            if (!cleanImages)
                return;

            string[] imagesExtensions = { $"*.{KnownFormats.svg}", $"*.{KnownFormats.png}" };
            CleanDirectory(imagesDirectoryPath, imagesExtensions);
        }
        public void RemoveEmptyDirectories()
        {
            void RemoveDirectoryWithMeta(string path)
            {
                FileUtil.DeleteFileOrDirectory(path);
                string meta = $"{path}.{KnownFormats.meta}";

                if (File.Exists(meta))
                    FileUtil.DeleteFileOrDirectory(meta);
            }

            void RemoveEmptyDirectory(string path, bool recursive)
            {
                if (!Directory.Exists(path))
                    return;

                if (recursive)
                {
                    Stack<string> directories = new();
                    directories.Push(path);

                    for (int depth = 0; depth < Const.maximumAllowedDepthLimit; depth++)
                    {
                        if (directories.Count == 0)
                            return;

                        path = directories.Pop();

                        if (!Directory.EnumerateFileSystemEntries(path).Any())
                            RemoveDirectoryWithMeta(path);
                        else
                            foreach (string subDirectory in Directory.EnumerateDirectories(path))
                                directories.Push(subDirectory);
                    }

                    throw new InvalidOperationException(Const.maximumDepthLimitReachedExceptionMessage);
                }

                if (Directory.EnumerateFileSystemEntries(path).Any())
                    return;

                RemoveDirectoryWithMeta(path);
            }

            RemoveEmptyDirectory(componentsDirectoryPath, false);
            RemoveEmptyDirectory(elementsDirectoryPath, false);
            RemoveEmptyDirectory(framesDirectoryPath, true);
            RemoveEmptyDirectory(imagesDirectoryPath, false);
        }
        #endregion

        #region Support Methods
        async Task DownloadDocumentsAsync(CancellationToken token)
        {
            async Task<IEnumerable<Nodes.Document>> GetMissingComponentsAsync(IEnumerable<string> components) =>
                (await Task.WhenAll(components.Chunk(maxComponentsIdsInOneRequest)
                                              .Select(chunk => GetAsync<Nodes>($"files/{fileKey}/nodes?ids={string.Join(",", chunk.Distinct())}", token))))
                .SelectMany(node => node.nodes.Values.Where(value => value != null));

            if (nodesRegistry.MissingComponents.Count > 0)
                foreach (Nodes.Document value in await GetMissingComponentsAsync(nodesRegistry.MissingComponents))
                    if (value.document is ComponentNode componentDoc)
                        stylesPreprocessor.AddMissingComponent(componentDoc, value.styles);
        }
        async Task GetImageFillsAsync(int progress, List<IBaseNodeMixin> imageFills, CancellationToken token)
        {
            IBaseNodeMixin[] nodes = imageFills.Where(x => nodeMetadata.ShouldDownload(x, UxmlDownloadImages.ImageFills)).ToArray();

            if (!nodes.Any())
                return;

            Data.Images filesImages = await GetAsync<Data.Images>($"files/{fileKey}/images", token);
            IEnumerable<string> imageRefs = nodes.OfType<IGeometryMixin>().Select(y => y.fills.OfType<ImagePaint>().First().imageRef);

            IEnumerable<KeyValuePair<string, string>> urls = filesImages.meta.images.Where(item => imageRefs.Contains(item.Key));
            await urls.ForEachParallelAsync(maxConcurrentRequests, x => GetImageAsync(x.Key, x.Value, KnownFormats.png, progress, token), token);
        }
        async Task GetImageNodesAsync(int progress, IEnumerable<IBaseNodeMixin> targetNodes, UxmlDownloadImages downloadImages, string extension, CancellationToken token)
        {
            IBaseNodeMixin[] nodes = targetNodes.Where(x => nodeMetadata.ShouldDownload(x, downloadImages)).ToArray();

            if (!nodes.Any())
                return;

            int i = 0;
            IEnumerable<IGrouping<int, string>> groups = nodes.Select(x => x.id).GroupBy(_ => i++ / 100);

            Task<Images>[] tasks = groups.Select(x => GetAsync<Images>($"images/{fileKey}?ids={string.Join(",", x)}&format={extension}", token)).ToArray();
            await Task.WhenAll(tasks);

            IEnumerable<KeyValuePair<string, string>> urls = tasks.SelectMany(x => x.Result.images);
            await urls.ForEachParallelAsync(maxConcurrentRequests, x => GetImageAsync(x.Key, x.Value, extension, progress, token), token);
        }
        async Task GetImageAsync(string nodeID, string url, string extension, int progress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogWarning($"Node {nodeID} does not have any URL for image. Most likely, this node uses a mask.");
                return;
            }

            bool fileExists = assetsInfo.GetAssetPath(nodeID, extension, out string assetPath);

            if (assetsInfo.modifiedContent.Contains(assetsInfo.GetAbsolutePath(assetPath)))
                return;

            Progress.SetStepLabel(progress, url);

            using HttpRequestMessage request = new(HttpMethod.Get, url);

            if (fileExists && assetsInfo.cachedAssets.Map.TryGetValue(nodeID, out string etag))
                request.Headers.Add("If-None-Match", $"\"{etag}\"");

            HttpResponseMessage response = await httpClient.SendAsync(request, token);

            bool isResolved = assetsInfo.cachedAssets.Map.ContainsValue(nodeID);
            if (response.Headers.TryGetValues("ETag", out IEnumerable<string> values))
                assetsInfo.cachedAssets[nodeID] = values.First().Trim('"');

            assetsInfo.GetAssetPath(nodeID, extension, out assetPath);
            string path = assetsInfo.GetAbsolutePath(assetPath);
            if (assetsInfo.modifiedContent.Contains(path))
                return;

            assetsInfo.modifiedContent.Add(path);

            if (response.IsSuccessStatusCode && !isResolved)
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    Debug.LogWarning($"Response is empty for node={nodeID}, url={url}");
                    if (extension == KnownFormats.svg)
                        await File.WriteAllTextAsync(path, InvalidSvg, token);
                    else
                        await File.WriteAllBytesAsync(path, InvalidPng, token);
                }
                else
                {
                    try
                    {
                        await File.WriteAllBytesAsync(path, bytes, token);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
            }
        }
        async Task WriteGradientsAsync(CancellationToken token)
        {
            foreach ((string key, GradientPaint gradient) in nodesRegistry.Gradients)
            {
                assetsInfo.GetAssetPath(key, KnownFormats.svg, out string relativePath);
                string xmlPath = assetsInfo.GetAbsolutePath(relativePath);
                using GradientWriter writer = new(xmlPath);
                await writer.WriteAsync(gradient, token);
                assetsInfo.modifiedContent.Add(xmlPath);
            }
        }
        #endregion
    }
}