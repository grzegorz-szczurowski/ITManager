using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;
using System.Collections.Generic;

namespace ITManager.Models.Navigation
{
    public enum AppMenuActionType
    {
        Navigate = 0,
        Dialog = 1
    }

    public sealed class AppMenuSection
    {
        public required string Key { get; set; }
        public required string Title { get; set; }
        public required string Icon { get; set; }
        public bool ExpandedByDefault { get; set; } = true;
        public List<AppMenuItem> Items { get; set; } = new();
    }

    public sealed class AppMenuItem
    {
        public required string Title { get; set; }
        public required string Href { get; set; }
        public required string Icon { get; set; }
        public NavLinkMatch Match { get; set; } = NavLinkMatch.Prefix;

        public bool IsVisible { get; set; } = true;

        public AppMenuActionType ActionType { get; set; } = AppMenuActionType.Navigate;

        public string? DialogKey { get; set; }

        public List<string> RequiredAllPermissions { get; set; } = new();
        public List<string> RequiredAnyPermissions { get; set; } = new();

        public string ItemKey => (Href ?? string.Empty).Trim().ToLowerInvariant();
    }

    public static class AppMenuConfig
    {
        public static List<AppMenuSection> BuildDefault()
        {
            return new List<AppMenuSection>
            {
                new AppMenuSection
                {
                    Key = "overview",
                    Title = "Overview",
                    Icon = Icons.Material.Filled.ViewModule,
                    ExpandedByDefault = true,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Dashboards",
                            Href = "/dashboard",
                            Icon = Icons.Material.Filled.DashboardCustomize,
                            Match = NavLinkMatch.All,
                            RequiredAllPermissions = new List<string> { "Dashboard.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Reports",
                            Href = "/reports",
                            Icon = Icons.Material.Filled.BarChart,
                            RequiredAllPermissions = new List<string> { "Reports.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Systems status",
                            Href = "/systems-status",
                            Icon = Icons.Material.Filled.MonitorHeart,
                            RequiredAllPermissions = new List<string> { "IT.SystemsStatus.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Contacts",
                            Href = "/contacts",
                            Icon = Icons.Material.Filled.Contacts,
                            RequiredAllPermissions = new List<string> { "Contacts.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "my-workspace",
                    Title = "My workspace",
                    Icon = Icons.Material.Filled.Person,
                    ExpandedByDefault = true,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
{
    Title = "Create ticket",
    Href = "/tickets/new",
    Icon = Icons.Material.Filled.AddCircle,
    ActionType = AppMenuActionType.Navigate,   // było Dialog
    RequiredAnyPermissions = new List<string>
    {
        "Tickets.Create.Own",
        "Tickets.Edit.Own",
        "Tickets.Edit.Assigned",
        "Tickets.Edit.Team",
        "Tickets.Edit.All"
    }
},
                        new AppMenuItem
                        {
                            Title = "My tickets",
                            Href = "/tickets/mine",
                            Icon = Icons.Material.Filled.Assignment,
                            RequiredAnyPermissions = new List<string>
                            {
                                "Tickets.View.Own",
                                "Tickets.Create.Own",
                                "Tickets.Edit.Own",
                                "Tickets.Edit.All"
                            }
                        },
                        new AppMenuItem
                        {
                            Title = "My team's tickets",
                            Href = "/tickets/my-team-tickets",
                            Icon = Icons.Material.Filled.Assignment,
                            RequiredAnyPermissions = new List<string>
                            {
                                "Tickets.View.Team",
                                "Tickets.Edit.Team",
                                "Tickets.Assign.Team",
                                "Tickets.Edit.All"
                            }
                        },
                        new AppMenuItem
                        {
                            Title = "My IT equipment",
                            Href = "/assets/my-assets",
                            Icon = Icons.Material.Filled.Computer,
                            RequiredAllPermissions = new List<string> { "Assets.MyAssets.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "tickets",
                    Title = "Tickets",
                    Icon = Icons.Material.Filled.ConfirmationNumber,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "All",
                            Href = "/tickets",
                            Icon = Icons.Material.Filled.ConfirmationNumber,
                            RequiredAllPermissions = new List<string> { "Tickets.Edit.All" }
                        },
                        
                        new AppMenuItem
                        {
                            Title = "Assigned to me",
                            Href = "/tickets/assigned",
                            Icon = Icons.Material.Filled.AssignmentInd,
                            RequiredAnyPermissions = new List<string>
                            {
                                "Tickets.View.Assigned",
                                "Tickets.Edit.Assigned",
                                "Tickets.Edit.Team",
                                "Tickets.Assign.Team",
                                "Tickets.Edit.All"
                            }
                        },

                        new AppMenuItem
                        {
                            Title = "New tickets",
                            Href = "/tickets/queue",
                            Icon = Icons.Material.Filled.Inbox,
                            RequiredAnyPermissions = new List<string>
                            {
                                "Tickets.Assign.Team",
                                "Tickets.Edit.Team",
                                "Tickets.Edit.All"
                            }
                        },

                        new AppMenuItem
                        {
                            Title = "Ticket scheduler",
                            Href = "/tickets/scheduler",
                            Icon = Icons.Material.Filled.Schedule,
                            RequiredAllPermissions = new List<string> { "Tickets.Scheduler.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "projects",
                    Title = "Projects",
                    Icon = Icons.Material.Filled.Workspaces,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Projects list",
                            Href = "/projects",
                            Icon = Icons.Material.Filled.FolderSpecial,
                            RequiredAllPermissions = new List<string> { "Projects.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "security-center",
                    Title = "SecurityCenter",
                    Icon = Icons.Material.Filled.Security,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Backups",
                            Icon = Icons.Material.Filled.Backup,
                            Href = "/security/backups",
                            Match = NavLinkMatch.Prefix,
                            RequiredAllPermissions = new List<string> { "Security.Backups.View" },
                            IsVisible = true
                        }
                    }
                },

                new AppMenuSection
                {
                    Key = "it-resources",
                    Title = "IT Resources",
                    Icon = Icons.Material.Filled.SupportAgent,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Devices",
                            Href = "/assets/devices",
                            Icon = Icons.Material.Filled.DevicesOther,
                            RequiredAllPermissions = new List<string> { "Assets.Devices.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Licenses",
                            Href = "/assets/licenses",
                            Icon = Icons.Material.Filled.VpnKey,
                            RequiredAllPermissions = new List<string> { "Assets.Licenses.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Systems",
                            Href = "/assets/systems",
                            Icon = Icons.Material.Filled.Dns,
                            RequiredAllPermissions = new List<string> { "Assets.Systems.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "SIM cards",
                            Href = "/assets/simcards",
                            Icon = Icons.Material.Filled.SimCard,
                            RequiredAllPermissions = new List<string> { "Assets.Sim.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Agreements",
                            Href = "/assets/agreements",
                            Icon = Icons.Material.Filled.Handshake,
                            RequiredAllPermissions = new List<string> { "Assets.Agreements.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Users",
                            Href = "/assets/users",
                            Icon = Icons.Material.Filled.People,
                            RequiredAllPermissions = new List<string> { "Assets.Users.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "operations",
                    Title = "Operations",
                    Icon = Icons.Material.Filled.Factory,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Parts Finder",
                            Href = "/wms/parts",
                            Icon = Icons.Material.Filled.Search,
                            RequiredAllPermissions = new List<string> { "Operations.Parts.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Containers",
                            Href = "/containers",
                            Icon = Icons.Material.Filled.Inventory2,
                            RequiredAllPermissions = new List<string> { "Operations.Containers.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Warehouse tools",
                            Href = "/warehouse",
                            Icon = Icons.Material.Filled.Warehouse,
                            RequiredAllPermissions = new List<string> { "Operations.WarehouseTools.View" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "administration",
                    Title = "Administration",
                    Icon = Icons.Material.Filled.AdminPanelSettings,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "Users & roles",
                            Href = "/admin/users-roles",
                            Icon = Icons.Material.Filled.ManageAccounts,
                            RequiredAllPermissions = new List<string> { "Admin.Permissions.Manage" }
                        },
                        new AppMenuItem
                        {
                            Title = "Permissions",
                            Href = "/admin/permissions",
                            Icon = Icons.Material.Filled.Security,
                            RequiredAllPermissions = new List<string> { "Admin.Permissions.Manage" }
                        },
                        new AppMenuItem
                        {
                            Title = "Dictionaries",
                            Href = "/admin/dictionaries",
                            Icon = Icons.Material.Filled.MenuBook,
                            RequiredAllPermissions = new List<string> { "Admin.Dictionaries.Manage" }
                        },
                    }
                },

                new AppMenuSection
                {
                    Key = "system-settings",
                    Title = "System settings",
                    Icon = Icons.Material.Filled.Settings,
                    ExpandedByDefault = false,
                    Items = new List<AppMenuItem>
                    {
                        new AppMenuItem
                        {
                            Title = "System",
                            Href = "/user/system",
                            Icon = Icons.Material.Filled.Settings,
                            RequiredAllPermissions = new List<string> { "Settings.View" }
                        },
                        new AppMenuItem
                        {
                            Title = "Profile",
                            Href = "/user/profile",
                            Icon = Icons.Material.Filled.Settings,
                            RequiredAllPermissions = new List<string> { "Settings.View" }
                        },
                    }
                },
            };
        }
    }
}
