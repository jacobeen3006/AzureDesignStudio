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
                        await openScan(Services!);
                    }
                    break;
                default:
                    {
                        await ResetDrawerRef(string.Empty);
                        break;
                    }
            }
        }

        private Task CloseDrawer()
        {
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
            dr.OnClose = CloseDrawer;
            return dr;
        }
        private async Task<bool> ResetDrawerRef(string drawerName)
        {
            if (openedDrawer == drawerName)
                return false;
            openedDrawer = drawerName;
            if (drawerRef is not null)
            {
                await drawerRef.CloseAsync();
                drawerRef = null;
            }

            return true;
        }
        private async Task OpenUserDrawer()
        {
            if (!await ResetDrawerRef("user"))
                return;

            drawerRef = await OpenDrawer<UserDrawerTemplate>("User account", "user");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
            };
        }
        private async Task OpenExportDrawer()
        {
            if (!await ResetDrawerRef("export"))
                return;

            drawerRef = await OpenDrawer<ExportDrawerTemplate>("Export the design", "export");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();

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
                };
            };
        }
        private async Task OpenImportDrawer()
        {
            if (!await ResetDrawerRef("import"))
                return;

            drawerRef = await OpenDrawer<ImportAdsDrawerTemplate>("Import a text design (.ads.json)", "import", width: 480);
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();

                if (string.IsNullOrEmpty(result))
                    return;

                AdsDesignFile file;
                try
                {
                    file = AdsDesignText.Read(result);
                }
                catch (AdsDesignFormatException e)
                {
                    messageService.Error(e.Message);
                    return;
                }

                if (adsContext.Diagram.Groups.Count > 0 || adsContext.Diagram.Nodes.Count > 0)
                    messageService.Info("The imported design replaced the current canvas.");

                var diagramGraph = file.Graph;
                adsContext.Diagram.History.Suspend(() => DataModelFactory.LoadDiagramFromDto(adsContext.Diagram, diagramGraph, mapper));
                adsContext.Diagram.History.Clear();
                adsContext.CurrentDesignName = string.IsNullOrWhiteSpace(file.Name) ? "imported design" : file.Name;
            };
        }
        private async Task OpenNamingDrawer()
        {
            if (!await ResetDrawerRef("names"))
                return;

            drawerRef = await OpenDrawer<NamingDrawerTemplate>("Globally unique names", "names", width: 480);
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
            };
        }
        private async Task OpenValidationDrawer()
        {
            if (!await ResetDrawerRef("validate"))
                return;

            var issues = AzureDesignStudio.Core.Validation.DesignValidator
                .Validate(adsContext.Diagram.Nodes.Concat(adsContext.Diagram.Groups));
            var options = System.Text.Json.JsonSerializer.Serialize(issues, AzureDesignStudio.Core.Validation.ValidationJson.Options);

            drawerRef = await OpenDrawer<ValidationDrawerTemplate>("Validate the design", options, width: 480);
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
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
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
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
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var splits = result.Split(':');
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
                messageService.Error($"Failed to save the design. Error code: {statusCode}");
            }
        }

        private async Task LoadDiagram(string designName)
        {
            if (string.IsNullOrEmpty(designName))
            {
                logger.LogInformation($"The file path is null or empty.");
                return;
            }

            // Clear the diagram before loading a new one; history tracks user edits only.
            adsContext.Diagram.History.Suspend(() =>
            {
                adsContext.Diagram.Nodes.Clear();
                adsContext.Diagram.Links.Clear();
                adsContext.Diagram.Groups.Clear();
            });

            messageService.Loading("Loading the design ...", 0);

            DiagramGraph? diagramGraph = null;
            var (status, designData) = await designService.LoadDesign(designName);
            if (status == 200 && !string.IsNullOrEmpty(designData))
            {
                diagramGraph = JsonSerializer.Deserialize<DiagramGraph>(designData);
            }

            if (diagramGraph == null)
            {
                messageService.Error("Cannot load the diagram.");
            }
            else
            {
                adsContext.Diagram.History.Suspend(() => DataModelFactory.LoadDiagramFromDto(adsContext.Diagram, diagramGraph!, mapper));
                adsContext.Diagram.History.Clear();
                adsContext.CurrentDesignName = designName;
            }

    }
    }
}
