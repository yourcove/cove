import type { ReactNode } from "react";
import { getBuiltInNavigationIcon } from "./navigationItems";

export interface EntityDetailTab {
  key: string;
  label: string;
  icon?: ReactNode;
  count?: number;
  disabled?: boolean;
}

interface EntityDetailTabsProps {
  tabs: EntityDetailTab[];
  activeTab: string;
  onTabChange: (key: string) => void;
  className?: string;
}

export function EntityDetailTabs({ tabs, activeTab, onTabChange, className = "" }: EntityDetailTabsProps) {
  if (tabs.length === 0) {
    return null;
  }

  return (
    <div className={["w-full border-b border-border", className].filter(Boolean).join(" ")}>
      <div className="overflow-x-auto px-1 [mask-image:linear-gradient(to_right,transparent,black_12px,black_calc(100%-12px),transparent)] sm:px-0 sm:[mask-image:none]">
        <div className="flex w-max min-w-full gap-1 sm:min-w-max" role="tablist" aria-label="Detail tabs">
          {tabs.map((tab) => {
            const isActive = activeTab === tab.key;
            const NavigationIcon = tab.icon ? undefined : getBuiltInNavigationIcon(tab.key);
            const icon = tab.icon ?? (NavigationIcon ? <NavigationIcon className="h-4 w-4" /> : null);
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                aria-selected={isActive}
                aria-label={tab.label}
                disabled={tab.disabled}
                onClick={() => onTabChange(tab.key)}
                className={[
                  "inline-flex min-h-11 shrink-0 items-center justify-center gap-2 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors sm:min-h-10 sm:px-4 sm:py-3",
                  isActive
                    ? "border-accent text-foreground"
                    : "border-transparent text-secondary hover:border-muted hover:text-foreground",
                  tab.disabled ? "cursor-not-allowed opacity-50" : "cursor-pointer",
                ].join(" ")}
              >
                {icon ? <span className="shrink-0 text-current">{icon}</span> : null}
                <span className="max-w-[8.5rem] truncate sm:max-w-none">{tab.label}</span>
                {typeof tab.count === "number" ? (
                  <span className="rounded-full bg-card px-2 py-0.5 text-xs text-muted">{tab.count}</span>
                ) : null}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
