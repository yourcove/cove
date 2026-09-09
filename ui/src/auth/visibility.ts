export type PermissionChecker = (permission: string) => boolean;
export type NavVisibilityUser = {
  kind?: string;
  readGrantedEntityKinds?: string[];
};

export type EntityResource =
  "video" | "audio" | "text" | "image" | "performer" | "gallery" | "studio" | "tag" | "group" | "segment" | "face";
export type NavPage =
  | "videos"
  | "audios"
  | "texts"
  | "segments"
  | "images"
  | "faces"
  | "galleries"
  | "performers"
  | "studios"
  | "tags"
  | "groups";

const READ_PERMISSIONS: Record<EntityResource, string> = {
  video: "videos.read",
  audio: "audios.read",
  text: "texts.read",
  image: "images.read",
  performer: "performers.read",
  face: "faces.read",
  gallery: "galleries.read",
  studio: "studios.read",
  tag: "tags.read",
  group: "groups.read",
  segment: "segments.read",
};

const WRITE_PERMISSIONS: Record<EntityResource, string> = {
  video: "videos.write",
  audio: "audios.write",
  text: "texts.write",
  image: "images.write",
  performer: "performers.write",
  face: "faces.write",
  gallery: "galleries.write",
  studio: "studios.write",
  tag: "tags.write",
  group: "groups.write",
  segment: "segments.write",
};

const DELETE_PERMISSIONS: Record<EntityResource, string> = {
  video: "videos.delete",
  audio: "audios.delete",
  text: "texts.delete",
  image: "images.delete",
  performer: "performers.delete",
  face: "faces.delete",
  gallery: "galleries.delete",
  studio: "studios.delete",
  tag: "tags.delete",
  group: "groups.delete",
  segment: "segments.delete",
};

const NAV_PAGE_RESOURCE: Record<NavPage, EntityResource> = {
  videos: "video",
  audios: "audio",
  texts: "text",
  segments: "segment",
  images: "image",
  faces: "face",
  galleries: "gallery",
  performers: "performer",
  studios: "studio",
  tags: "tag",
  groups: "group",
};

export function canReadEntity(resource: EntityResource, hasPermission: PermissionChecker) {
  return hasPermission(READ_PERMISSIONS[resource]);
}

export function canWriteEntity(resource: EntityResource, hasPermission: PermissionChecker) {
  return hasPermission(WRITE_PERMISSIONS[resource]);
}

export function canDeleteEntity(resource: EntityResource, hasPermission: PermissionChecker) {
  return hasPermission(DELETE_PERMISSIONS[resource]);
}

export function canShowNavPage(page: NavPage, hasPermission: PermissionChecker, user?: NavVisibilityUser | null) {
  if (user?.kind === "shareLink") {
    return (user.readGrantedEntityKinds ?? []).includes(NAV_PAGE_RESOURCE[page]);
  }

  return canReadEntity(NAV_PAGE_RESOURCE[page], hasPermission);
}

export function hasAnyPermission(hasPermission: PermissionChecker, permissions: string[]) {
  return permissions.some((permission) => hasPermission(permission));
}

export function filterItemsByPermission<T extends { key: string }>(
  items: T[],
  permissionsByKey: Partial<Record<string, string>>,
  hasPermission: PermissionChecker,
) {
  return items.filter((item) => {
    const requiredPermission = permissionsByKey[item.key];
    return !requiredPermission || hasPermission(requiredPermission);
  });
}
