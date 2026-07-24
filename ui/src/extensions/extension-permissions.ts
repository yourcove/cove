type PermissionGatedContribution = {
  requiredPermission?: string;
  requiredPermissions?: string[];
  requiredPermissionMode?: "all" | "any";
};

function getRequiredPermissions(contribution: PermissionGatedContribution): string[] {
  if (contribution.requiredPermissions?.length) {
    return contribution.requiredPermissions;
  }

  return contribution.requiredPermission ? [contribution.requiredPermission] : [];
}

export function canAccessExtensionContribution(
  contribution: PermissionGatedContribution,
  hasPermission: (permission: string) => boolean,
): boolean {
  const requiredPermissions = getRequiredPermissions(contribution);
  if (requiredPermissions.length === 0) {
    return true;
  }

  switch (contribution.requiredPermissionMode) {
    case "any":
      return requiredPermissions.some(hasPermission);
    case "all":
    case undefined:
      return requiredPermissions.every(hasPermission);
    default:
      return false;
  }
}
