import { Film, Users, Building2, Tags, Image, ImageIcon, Layers, Settings, BarChart3, Activity, Bookmark, HelpCircle, Menu, X } from "lucide-react";
import { useState } from "react";
import { JobDrawer, useJobCount } from "./JobDrawer";
import { GlobalSearch } from "./GlobalSearch";
import { useRouteRegistry } from "../router/RouteRegistry";
import { useAppConfig } from "../state/AppConfigContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import { KeyboardShortcutsDialog } from "./KeyboardShortcutsDialog";

interface NavbarProps {
  currentPage: string;
  navigate: (r: any) => void;
}

const navItems = [
  { page: "scenes", label: "Scenes", icon: Film },
  { page: "images", label: "Images", icon: ImageIcon },
  { page: "markers", label: "Markers", icon: Bookmark },
  { page: "galleries", label: "Galleries", icon: Image },
  { page: "performers", label: "Performers", icon: Users },
  { page: "studios", label: "Studios", icon: Building2 },
  { page: "tags", label: "Tags", icon: Tags },
  { page: "groups", label: "Groups", icon: Layers },
];

const DETAIL_PARENT_PAGE: Record<string, string> = {
  scene: "scenes",
  image: "images",
  performer: "performers",
  gallery: "galleries",
  studio: "studios",
  tag: "tags",
  group: "groups",
};

export function Navbar({ currentPage, navigate }: NavbarProps) {
  const [jobDrawerOpen, setJobDrawerOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const jobCount = useJobCount();
  const { routes } = useRouteRegistry();
  const { config } = useAppConfig();
  const { manifest } = useExtensions();

  // Derive parent page: built-in detail pages use static map, extension detail
  // pages (showInNav=false) resolve to their extension's nav page (showInNav=true)
  let activePage = DETAIL_PARENT_PAGE[currentPage] ?? currentPage;
  if (activePage === currentPage && manifest) {
    const extPage = manifest.pages.find((p) => p.route === currentPage && !p.showInNav);
    if (extPage) {
      const navPage = manifest.pages.find((p) => p.extensionId === extPage.extensionId && p.showInNav);
      if (navPage) activePage = navPage.route;
    }
  }

  const enabledMenuItems = config?.interface.menuItems.length
    ? config.interface.menuItems
    : null;

  const enabledSet = enabledMenuItems ? new Set(enabledMenuItems) : null;

  const extensionNavItems = routes
    .filter((r) => r.navItem)
    .map((r) => ({ page: r.navItem!.page, label: r.navItem!.label, icon: r.navItem!.icon, order: r.navItem!.order ?? 99 }))
    .sort((a, b) => a.order - b.order);

  // Build ordered nav: if menuItems specifies order, use it; otherwise fall back to default
  const allItemsMap = new Map<string, typeof navItems[number] | typeof extensionNavItems[number]>();
  for (const item of navItems) allItemsMap.set(item.page, item);
  for (const item of extensionNavItems) allItemsMap.set(item.page, item);

  const allNavItems = enabledMenuItems
    ? enabledMenuItems.map((page) => allItemsMap.get(page)).filter(Boolean) as (typeof navItems[number] | typeof extensionNavItems[number])[]
    : [...navItems, ...extensionNavItems];

  return (
    <nav className="cove-navbar bg-nav sticky top-0 z-50 shadow-lg shadow-black/30" role="navigation" aria-label="Main navigation">
      <div className="w-full px-4">
        <div className="flex items-center h-12">
          {/* Logo */}
          <a
            href="/"
            onClick={(e) => { e.preventDefault(); navigate({ page: "home" }); }}
            className="flex items-center gap-2 mr-6 shrink-0 cursor-pointer"
          >
            <svg viewBox="0 0 347.11 91.99" className="h-8 text-accent" fill="currentColor" aria-hidden="true" style={{ width: "auto" }}>
              <path d="M151.33,91.45c-18.83,2.79-39.54-5.12-50.67-22.58-.64-1-.75-1.73-.04-2.83,2.63-4.07,5.1-8.25,7.64-12.38.3-.49.6-.96.95-1.52,1.21,2.58,2.23,5.08,3.52,7.43,11.87,21.61,43.51,23.89,58.4,4.24,8.25-10.89,7.39-26.67-2.02-37-16.5-18.13-44.49-15.01-56.98,6.48-3.89,6.7-7.15,13.76-10.88,20.55-3.64,6.64-7.16,13.38-12.1,19.2-11.47,13.54-26.02,19.99-43.77,18.62-15.73-1.21-28.44-8.19-37.55-21.13C-6.59,50.05,0,23.82,18.64,10,29.21,2.17,41.14-1.02,54.17.29c11.37,1.15,21.18,5.9,29.5,13.76.97.91,1.05,1.55.24,2.65-2.7,3.67-5.27,7.43-7.66,10.83-3.47-2.67-6.67-5.63-10.32-7.86-18.16-11.1-43.04-1.98-49.42,17.97-3.67,11.47.73,24.46,10.92,32.23,16.13,12.32,39.17,8.83,51.35-7.86,6.03-8.25,10.74-17.28,15.19-26.45,2.84-5.86,5.72-11.72,9.85-16.81,7.55-9.3,17.3-15.05,28.99-17.49,13.35-2.78,25.91-.6,37.35,6.8,11.82,7.65,19.42,18.35,21.29,32.51,2.1,15.89-3.5,29.01-15.35,39.51-7.03,6.23-15.36,9.88-24.8,11.34l.03.03Z"/>
              <path d="M296.84,15.08h-7.03v22.74h49.38v14.49h-49.39v24.35h57.31v14.29h-72.98c-.02-.48-.06-.93-.06-1.39,0-22.3-.02-44.61,0-66.91,0-.57.12-1.18.34-1.71,2.74-6.42,5.49-12.84,8.31-19.22.22-.49.97-1.07,1.48-1.07,20.29-.04,40.58-.02,60.86,0,.06,0,.12.02.29.06v14.38h-48.52,0Z"/>
              <path d="M216.22,38.1c5,11.29,9.93,22.46,14.99,33.91,2.4-5.6,4.68-10.92,6.96-16.23,7.69-17.94,15.39-35.87,23.06-53.82.43-1.02.95-1.41,2.09-1.39,4.98.07,9.96.03,15.1.03-.15.46-.23.84-.39,1.19-12.77,28.83-25.57,57.65-38.26,86.52-.92,2.08-1.89,2.97-4.24,2.81-3.54-.24-7.12-.13-10.67-.02-1.35.04-1.94-.46-2.47-1.64-10.4-23.21-20.85-46.41-31.28-69.6-2.74-6.1-5.5-12.19-8.24-18.29-.12-.26-.19-.53-.34-.97,5.53,0,10.91-.02,16.28.05.39,0,.94.6,1.14,1.05,5.43,12.09,10.82,24.19,16.29,36.41h-.02Z"/>
            </svg>
          </a>

          {/* Hamburger button - mobile only */}
          <button
            onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
            className="navbar-mobile-toggle p-2 rounded text-secondary hover:text-foreground mr-2"
            aria-expanded={mobileMenuOpen}
            aria-label="Toggle navigation menu"
          >
            {mobileMenuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
          </button>

          {/* Nav items - hidden on mobile */}
          <div className="navbar-desktop-links items-center gap-0.5 overflow-x-auto shrink-0">
            {allNavItems.map(({ page, label, icon: Icon }) => (
              <a
                key={page}
                href={`#/${page}`}
                onClick={(e) => { e.preventDefault(); navigate({ page }); }}
                aria-current={activePage === page ? "page" : undefined}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded text-sm font-medium whitespace-nowrap transition-colors cursor-pointer ${
                  activePage === page
                    ? "text-accent"
                    : "text-secondary hover:text-foreground"
                }`}
              >
                {Icon && <Icon className="w-4 h-4" />}
                {label}
              </a>
            ))}
          </div>

          {/* Spacer */}
          <div className="flex-1" />

          <GlobalSearch navigate={navigate} />

          {/* Actions */}
          <div className="flex items-center gap-1 shrink-0">
            {jobCount > 0 && (
              <button
                onClick={() => setJobDrawerOpen(true)}
                className="relative p-2 rounded text-secondary hover:text-foreground"
                title="Jobs"
              >
                <Activity className="w-[18px] h-[18px]" />
                <span className="absolute -top-0.5 -right-0.5 bg-accent text-white text-[10px] rounded-full w-4 h-4 flex items-center justify-center font-bold">
                  {jobCount}
                </span>
              </button>
            )}
            <a
              href="#/stats"
              onClick={(e) => { e.preventDefault(); navigate({ page: "stats" }); }}
              className={`p-2 rounded cursor-pointer ${
                currentPage === "stats" ? "text-accent" : "text-secondary hover:text-foreground"
              }`}
              title="Statistics"
            >
              <BarChart3 className="w-[18px] h-[18px]" />
            </a>
            <button
              onClick={() => setHelpOpen(true)}
              className="p-2 rounded text-secondary hover:text-foreground"
              title="Keyboard Shortcuts (?)"
            >
              <HelpCircle className="w-[18px] h-[18px]" />
            </button>
            <a
              href="#/settings"
              onClick={(e) => { e.preventDefault(); navigate({ page: "settings" }); }}
              className={`p-2 rounded cursor-pointer ${
                currentPage === "settings" ? "text-accent" : "text-secondary hover:text-foreground"
              }`}
              title="Settings"
            >
              <Settings className="w-[18px] h-[18px]" />
            </a>
          </div>
        </div>
      </div>
      <JobDrawer open={jobDrawerOpen} onClose={() => setJobDrawerOpen(false)} />
      <KeyboardShortcutsDialog open={helpOpen} onClose={() => setHelpOpen(false)} />
      {/* Mobile dropdown menu */}
      {mobileMenuOpen && (
        <div className="navbar-mobile-menu bg-nav border-t border-border shadow-lg">
          <div className="px-4 py-2 space-y-1">
            {allNavItems.map(({ page, label, icon: Icon }) => (
              <button
                key={page}
                onClick={() => { navigate({ page }); setMobileMenuOpen(false); }}
                className={`flex items-center gap-2 w-full px-3 py-2 rounded text-sm font-medium transition-colors ${
                  activePage === page
                    ? "text-accent bg-accent/10"
                    : "text-secondary hover:text-foreground hover:bg-surface"
                }`}
              >
                {Icon && <Icon className="w-4 h-4" />}
                {label}
              </button>
            ))}
          </div>
        </div>
      )}
    </nav>
  );
}
