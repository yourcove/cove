import { createRouteLinkProps } from "./cardNavigation";

export function RouteCardLinkOverlay({
  route,
  onClick,
  label,
  disabled,
  selectionSafeZone,
}: {
  route: { page: string; id: number };
  onClick: () => void;
  label: string;
  disabled?: boolean;
  selectionSafeZone?: boolean;
}) {
  if (disabled) {
    return null;
  }

  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, onClick);

  return (
    <a
      {...linkProps}
      aria-label={label}
      className="absolute inset-0 z-[1]"
      style={selectionSafeZone
        ? { clipPath: "polygon(42px 0, 100% 0, 100% 100%, 0 100%, 0 42px, 42px 42px)" }
        : undefined}
    />
  );
}

export function CardSelectionToggle({
  selected = false,
  selecting = false,
  onToggle,
}: {
  selected?: boolean;
  selecting?: boolean;
  onToggle?: () => void;
}) {
  if (!onToggle) {
    return null;
  }

  return (
    <button
      type="button"
      aria-label={selected ? "Deselect item" : "Select item"}
      aria-pressed={selected}
      onClick={(event) => {
        event.preventDefault();
        event.stopPropagation();
        onToggle();
      }}
      onPointerDown={(event) => event.stopPropagation()}
      className={`absolute left-0.5 top-0.5 z-10 flex h-8 w-8 items-center justify-center rounded-md transition-opacity ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"}`}
    >
      <span className={`flex h-4 w-4 items-center justify-center rounded border shadow-sm ${selected ? "border-accent bg-accent text-white" : "border-border bg-background/95 text-transparent"}`}>
        <svg viewBox="0 0 16 16" className="h-3 w-3" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M3.5 8.25 6.5 11.25 12.5 4.75" />
        </svg>
      </span>
    </button>
  );
}