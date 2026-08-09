import { useEffect } from "react";
import { useAppConfig } from "../state/AppConfigContext";

function getAppTitle(configuredTitle: string | null | undefined) {
  const trimmedTitle = configuredTitle?.trim();
  return trimmedTitle ? trimmedTitle : "Cove";
}

export function useDocumentTitle(pageTitle?: string | null, enabled = true) {
  const { config } = useAppConfig();
  const appTitle = getAppTitle(config?.ui.title);
  const trimmedPageTitle = pageTitle?.trim();

  useEffect(() => {
    if (!enabled) return;
    if (trimmedPageTitle) {
      document.body.dataset.covePageTitle = trimmedPageTitle;
    } else {
      delete document.body.dataset.covePageTitle;
    }

    document.title = trimmedPageTitle ? `${trimmedPageTitle} | ${appTitle}` : appTitle;

    return () => {
      if (document.body.dataset.covePageTitle === trimmedPageTitle) {
        delete document.body.dataset.covePageTitle;
      }

      document.title = appTitle;
    };
  }, [appTitle, enabled, trimmedPageTitle]);
}
