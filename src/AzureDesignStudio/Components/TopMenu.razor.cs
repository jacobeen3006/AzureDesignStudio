using AntDesign;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.Cost;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Microsoft.JSInterop;
using System.Text.Json;
using Blazor.Diagrams.Core.Models;
using AzureDesignStudio.Components.MenuDrawer;
using AzureDesignStudio.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Services;
using AzureDesignStudio.Core.Common;
namespace AzureDesignStudio.Components
{
    public partial class TopMenu
    {
        private DrawerRef<string>? drawerRef;
        private string openedDrawer = string.Empty;
        private string? imgUrl = null;

        protected override void OnInitialized()
        {
            var commands = new List<AdsCommand>
            {
                new("save", "Save / load design", "Open the save and load drawer", "Design", "▣", OpenSaveDrawer),
                new("export", "Export the design", "ARM, Bicep, image, or .ads.json", "Design", "⇪", OpenExportDrawer),
                new("validate", "Validate the design", "Check rules and blocked resources", "Design", "✓", OpenValidationDrawer),
                new("cost", "Estimate monthly cost", "Price the nodes on the canvas", "FinOps", "$", OpenCostDrawer),
                new("compare", "Compare saved designs", "Side-by-side design diff", "Analyze", "⇄", OpenCompareDrawer),
                new("import", "Import a text design", "Paste and validate .ads.json", "Design", "↧", OpenImportDrawer),
                new("names", "Suggest globally unique names", "Naming for DNS-backed types", "Design", "#", OpenNamingDrawer),
                new("guardrails", "Do-not-touch guardrails", "Protected scopes, tags, and types", "Safety", "⛨", OpenGuardrailsDrawer),
                new("user", "Account", "Sign-in and identity", "Account", "◉", OpenUserDrawer),
            };

            if (features.TenantScanLabel is { } label && features.OpenTenantScan is { } openScan)
            {
                commands.Add(new AdsCommand("scan", label, "Scan a tenant and browse inventory",
                    "Analyze", "◈", () => openScan(Services!)));
            }

            commandService.RegisterRange(commands);
        }

        private async Task HandleMenuItemClicked(MenuItem menuItem)
        {
            switch (menuItem.Key)
            {
                case "export":
                    await OpenExportDrawer();
                    break;
                case "validate":
                    await OpenValidationDrawer();
                    break;
                case "cost":
                    await OpenCostDrawer();
                    break;
                case "save":
                    await OpenSaveDrawer();
                    break;
                case "guardrails":
                    await OpenGuardrailsDrawer();
                    break;
                case "compare":
                    await OpenCompareDrawer();
                    break;
                case "import":
                    await OpenImportDrawer();
                    break;
                case "names":
                    await OpenNamingDrawer();
                    break;
                case "user":
                    await OpenUserDrawer();
                    break;
                case "scan":
                    if (features.OpenTenantScan is { } openScan)
                    {
                        // The host opens this one itself; close ours first so they do not overlap.
                        await ResetDrawerRef(string.Empty);
                        await openScan(Services!);
                    }
                    break;
                case "workspace":
                    if (features.OpenWorkspaceData is { } openWorkspaceData)
                    {
                        // Opened by the host too; close ours first so they do not overlap.
                        await ResetDrawerRef(string.Empty);
                        await openWorkspaceData(Services!);
                    }
                    break;
                default:
                    {
                        await ResetDrawerRef(string.Empty);
                        break;
                    }
            }
        }

        /// <summary>Forgets a drawer that has closed - but only while it is still the current
        /// one. Switching from drawer A to B closes A, and A's close handlers then ran against
        /// B: they cleared the reference to B, so the next switch could not close it and the
        /// drawers piled up, and they cleared its name, so clicking B's menu item opened a
        /// second copy.</summary>
        private Task CloseDrawer(DrawerRef<string> closing)
        {
            if (!ReferenceEquals(drawerRef, closing))
                return Task.CompletedTask;

            // Deselect the ant menu item. A bit strange way.
            // Tracked here: https://github.com/ant-design-blazor/ant-design-blazor/issues/2159
            // BL0005: setting the Menu parameter from outside is the only known workaround.
#pragma warning disable BL0005
            if (topMenu != null) topMenu.SelectedKeys = Array.Empty<string>();
#pragma warning restore BL0005
            drawerRef = null;
            openedDrawer = string.Empty;
            return Task.CompletedTask;
        }
        private async Task<DrawerRef<string>> OpenDrawer<TDrawerTemplate>(string title, string options,
            bool bodyNoPadding = false, int width = 350)
            where TDrawerTemplate : FeedbackComponent<string, string>
        {
            var drawerOptions = new DrawerOptions()
            {
                Title = title,
                Width = width.ToString(),
            };
            if (bodyNoPadding)
                drawerOptions.BodyStyle = "padding:0px;";

            var dr = await drawerService.CreateAsync<TDrawerTemplate, string, string>(drawerOptions, options);
            dr.OnClose = () => CloseDrawer(dr);
            return dr;
        }
        private async Task<bool> ResetDrawerRef(string drawerName)
        {
            if (openedDrawer == drawerName)
                return false;

            // Detach first: the closing drawer's handlers must find that it is no longer current.
            var previous = drawerRef;
            drawerRef = null;
            if (previous is not null)
                await previous.CloseAsync();
            openedDrawer = drawerName;

            return true;
        }
        private async Task OpenUserDrawer()
        {
            if (!await ResetDrawerRef("user"))
                return;

            drawerRef = await OpenDrawer<UserDrawerTemplate>("User account", "user");
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);
            };
        }
        private async Task OpenExportDrawer()
        {
            if (!await ResetDrawerRef("export"))
                return;

            drawerRef = await OpenDrawer<ExportDrawerTemplate>("Export the design", "export");
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);

                if (adsContext.Diagram.Groups.Count == 0 && adsContext.Diagram.Nodes.Count == 0)
                {
                    messageService.Warn("There is nothing to export.");
                    return;
                }

                switch (result)
                {
                    case "arm":
                        await ExportArmTemplate();
                        break;
                    case "bicep":
                        await ExportBicep();
                        break;
                    case "img":
                        await InvokePrintJS();
                        break;
                    case "ads":
                        await ExportAdsJson();
                        break;
                }
                ;
            };
        }
        private async Task OpenImportDrawer()
        {
            if (!await ResetDrawerRef("import"))
                return;

            drawerRef = await OpenDrawer<ImportAdsDrawerTemplate>("Import a text design (.ads.json)", "import", width: 480);
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);

                if (string.IsNullOrEmpty(result))
                    return;

                AdsDesignFile file;
                try
                {
                    file = AdsDesignText.Read(result);
                }
                catch (Exception e) when (e is AdsDesignFormatException or FormatException or JsonException or InvalidOperationException)
                {
                    // AdsDesignFormatException is the reader's own verdict; the others escape it
                    // for input such as {"version":1.5}.
                    messageService.Error(e is AdsDesignFormatException ? e.Message : "The file is not a readable design.");
                    return;
                }

                var hadContent = adsContext.Diagram.Groups.Count > 0 || adsContext.Diagram.Nodes.Count > 0;

                // Replace, as the notice below says: the import used to be added on top of
                // whatever was already on the canvas.
                var detached = new List<string>();
                var failure = await CanvasLoader.ReplaceAsync(adsContext.Diagram, file.Graph, mapper, detached);
                if (failure is not null)
                {
                    messageService.Error($"The design could not be imported: {failure}");
                    return;
                }
                if (detached.Count > 0)
                    messageService.Warning(new MessageConfig { Content = DetachedNotice(detached), Duration = 10 });

                if (hadContent)
                    messageService.Info("The imported design replaced the current canvas.");
                adsContext.CurrentDesignName = string.IsNullOrWhiteSpace(file.Name) ? "imported design" : file.Name;
            };
        }
        private async Task OpenNamingDrawer()
        {
            if (!await ResetDrawerRef("names"))
                return;

            drawerRef = await OpenDrawer<NamingDrawerTemplate>("Globally unique names", "names", width: 480);
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);
            };
        }
        private async Task OpenValidationDrawer()
        {
            if (!await ResetDrawerRef("validate"))
                return;

            var report = AzureDesignStudio.Core.Validation.ValidationReport
                .For(adsContext.Diagram.Nodes.Concat(adsContext.Diagram.Groups));
            var options = System.Text.Json.JsonSerializer.Serialize(report, AzureDesignStudio.Core.Validation.ValidationJson.Options);

            drawerRef = await OpenDrawer<ValidationDrawerTemplate>("Validate the design", options, width: 480);
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);
            };
        }

        private async Task OpenCostDrawer()
        {
            if (!await ResetDrawerRef("cost"))
                return;

            var inputs = adsContext.Diagram.Nodes.Concat(adsContext.Diagram.Groups)
                .Select(n => new CostNodeInput(n is IAzureResource r ? r.Name : n.Id, DataModelFactory.GetStencilKey(n), CostSkuResolver.Resolve(n)))
                .ToList();
            var options = System.Text.Json.JsonSerializer.Serialize(inputs, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            drawerRef = await OpenDrawer<CostDrawerTemplate>("Estimate monthly cost", options, width: 560);
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);
            };
        }
        private async Task<(string?, IArmTemplate?)> GetArmJson()
        {
            var armTemplate = new ArmTemplate();
            try
            {
                foreach (var group in adsContext.Diagram.Groups.Where(g => g.Group == null || g is not SubnetModel))
                {
                    if (group is IAzureResource res)
                    {
                        armTemplate.AddParameters(res.GetArmParameters());
                        armTemplate.AddResource(res.GetArmResources());
                    }
                }
                foreach (var node in adsContext.Diagram.Nodes.Where(n => n is not GroupModel))
                {
                    if (node is IAzureResource res)
                    {
                        armTemplate.AddParameters(res.GetArmParameters());
                        armTemplate.AddResource(res.GetArmResources());
                    }
                }
            }
            catch (Exception ex)
            {
                messageService.Error($"{ex.Message}");
                return (null, null);
            }

            return (armTemplate.GenerateArmTemplate(), armTemplate);
        }
        private async Task ExportBicep()
        {
            var (jsonString, armTemplate) = await GetArmJson();
            if (string.IsNullOrEmpty(jsonString))
            {
                return;
            }

            var modalRef = modalService.CreateModal(new ModalOptions
            {
                Centered = true,
                Footer = null,
                Closable = false,
                Visible = true,
                MaskClosable = false,
                Content = builder =>
                {
                    builder.OpenElement(0, "div");
                    builder.AddAttribute(1, "style", "text-align: center;");
                    builder.OpenComponent<Spin>(3);
                    builder.AddAttribute(4, "Tip", "Working hard on it...");
                    builder.CloseComponent();
                    builder.CloseElement();
                }
            });

            await Task.Delay(10);

            var bicep = await _adsBicepDecompiler.Decompile(jsonString);
            await modalRef.CloseAsync();

            if (!string.IsNullOrEmpty(bicep.Error))
            {
                logger.LogError("Decompile Bicep failed: {BicepError}", bicep.Error);
                messageService.Error($"{bicep.Error}");
                return;
            }

            await OpenCodeDrawer(new CodeDrawerContent
            {
                Type = CodeDrawerContentType.Bicep,
                Content = bicep.BicepFile!,
                ArmTemplate = armTemplate
            });
        }
        private async Task ExportArmTemplate()
        {
            var (jsonString, armTemplate) = await GetArmJson();
            if (string.IsNullOrEmpty(jsonString))
            {
                return;
            }

            await OpenCodeDrawer(new CodeDrawerContent
            {
                Type = CodeDrawerContentType.Json,
                Content = jsonString,
                ArmTemplate = armTemplate
            });
        }
        private async Task ExportAdsJson()
        {
            var diagramGraph = await DataModelFactory.SaveDiagramToDto(adsContext.Diagram, mapper);
            if (diagramGraph is null)
            {
                return;
            }

            await OpenCodeDrawer(new CodeDrawerContent
            {
                Type = CodeDrawerContentType.AdsJson,
                Content = AdsDesignText.Write(diagramGraph, adsContext.CurrentDesignName)
            });
        }
        private async Task OpenCodeDrawer(CodeDrawerContent content)
        {
            var currentWindowSize = await JS.InvokeAsync<WindowSize>("getWindowSize");
            var title = content.Type switch
            {
                CodeDrawerContentType.Json => _armTitle,
                CodeDrawerContentType.Bicep => _bicepTitle,
                CodeDrawerContentType.AdsJson => _adsTextTitle,
                _ => null
            };

            var options = new DrawerOptions
            {
                Title = title,
                Placement = DrawerPlacement.Bottom,
                Height = currentWindowSize.Height.ToString()
            };

            await drawerService.CreateAsync<CodeDrawerTemplate, CodeDrawerContent, string>(options, content);
        }

        private async Task OpenGuardrailsDrawer()
        {
            var currentWindowSize = await JS.InvokeAsync<WindowSize>("getWindowSize");
            var options = new DrawerOptions
            {
                Title = "Do-not-touch guardrails",
                Placement = DrawerPlacement.Bottom,
                Height = currentWindowSize.Height.ToString()
            };

            await drawerService.CreateAsync<GuardrailsDrawerTemplate, GuardrailsDrawerContent, string>(options, new GuardrailsDrawerContent());
        }

        private async Task OpenCompareDrawer()
        {
            var currentWindowSize = await JS.InvokeAsync<WindowSize>("getWindowSize");
            var options = new DrawerOptions
            {
                Title = "Compare saved designs",
                Placement = DrawerPlacement.Bottom,
                Height = currentWindowSize.Height.ToString()
            };

            await drawerService.CreateAsync<CompareDrawerTemplate, CompareDrawerContent, string>(options, new CompareDrawerContent());
        }

        private async Task InvokePrintJS()
        {
            var bound = adsContext.Diagram.BeforePrint();

            try
            {
                imgUrl = await JS.InvokeAsync<string>("diagram2PicAsync", "png", bound!.Width, bound!.Height);
            }
            catch (JSException ex)
            {
                logger.LogError("Export failed: {ExceptionMessage}", ex.Message);
            }
            finally
            {
                adsContext.Diagram.AfterPrint();
            }

            if (!string.IsNullOrEmpty(imgUrl))
            {
                showImgPreview = true;
                await InvokeAsync(StateHasChanged);
            }
        }

        private async Task OpenSaveDrawer()
        {
            if (!await ResetDrawerRef("save"))
                return;

            drawerRef = await OpenDrawer<SaveDrawerTemplate>("Save or load the design", "save", true);
            var opened = drawerRef;
            opened.OnClosed = async result =>
            {
                await CloseDrawer(opened);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    // Two parts only: a name with a colon in it is still one name.
                    var splits = result.Split(':', 2);
                    if (splits[0] == "save")
                        await SaveDiagram(splits[1]);
                    else
                        await LoadDiagram(splits[1]);
                }
            };
        }
        private async Task SaveDiagram(string designName)
        {
            if (string.IsNullOrWhiteSpace(designName))
            {
                logger.LogInformation($"Design name is null or empty.");
                return;
            }

            var diagramGraph = await DataModelFactory.SaveDiagramToDto(adsContext.Diagram, mapper);
            if (diagramGraph == null)
            {
                messageService.Warn("There is nothing to save.");
                return;
            }
            var graph = JsonSerializer.Serialize(diagramGraph);

            var statusCode = await designService.SaveDesign(designName, graph);
            if (statusCode >= 200 && statusCode <= 299)
            {
                messageService.Success(designService.LastCallWasOffline
                    ? "The design is saved on this device. It will sync when the server is reachable."
                    : "The design is saved successfully.");
            }
            else
            {
                messageService.Error($"The design was not saved: {DesignGrpcService.Describe(statusCode)}. It is still on the canvas.");
            }
        }

        private static string DetachedNotice(IReadOnlyList<string> detached) =>
            $"The design is loaded, but {detached.Count} item(s) referred to a parent group that is not in the file and were placed on the canvas directly: " +
            string.Join(", ", detached.Take(5)) + (detached.Count > 5 ? ", ..." : ".") + " Check them before saving.";

        private async Task LoadDiagram(string designName)
        {
            if (string.IsNullOrEmpty(designName))
            {
                logger.LogInformation($"The file path is null or empty.");
                return;
            }

            // One keyed message: the result replaces the "loading" notice. (Unkeyed and with
            // duration 0, that notice used to stay on screen after every load.)
            const string messageKey = "ads-design-load";
            messageService.Loading(new MessageConfig { Content = "Loading the design ...", Key = messageKey, Duration = 0 });

            // Fetch and parse first; the canvas is only touched once there is a design to show.
            // Clearing up front lost the unsaved canvas whenever the load then failed.
            DiagramGraph? diagramGraph = null;
            string? failure = null;
            try
            {
                var (status, designData) = await designService.LoadDesign(designName);
                if (status == 200 && !string.IsNullOrEmpty(designData))
                    diagramGraph = JsonSerializer.Deserialize<DiagramGraph>(designData);
                else
                    failure = $"Cannot load the design. Error code: {status}";
            }
            catch (Exception e) when (e is Grpc.Core.RpcException or HttpRequestException or JsonException)
            {
                logger.LogWarning(e, "Loading design {DesignName} failed.", designName);
                failure = e is JsonException ? "The saved design is not readable." : "Cannot reach the server to load the design.";
            }

            var detached = new List<string>();
            if (diagramGraph is not null)
                failure = await CanvasLoader.ReplaceAsync(adsContext.Diagram, diagramGraph, mapper, detached);
            else
                failure ??= "Cannot load the design.";

            if (failure is null)
            {
                adsContext.CurrentDesignName = designName;
                if (detached.Count == 0)
                    messageService.Success(new MessageConfig { Content = "The design is loaded.", Key = messageKey, Duration = 2 });
                else
                    messageService.Warning(new MessageConfig { Content = DetachedNotice(detached), Key = messageKey, Duration = 10 });
            }
            else
            {
                messageService.Error(new MessageConfig { Content = failure, Key = messageKey, Duration = 6 });
            }
        }
    }
}
