import { fireEvent, render, screen } from "@testing-library/react";
import type { ComponentProps, ComponentType, ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

interface OverrideRendererCall {
  targetComponent: string;
  componentProps: Record<string, unknown>;
  renderDefault: () => ReactNode;
}

const { overrideRendererCalls, renderDefaultMedia } = vi.hoisted(() => ({
  overrideRendererCalls: [] as OverrideRendererCall[],
  renderDefaultMedia: { current: false },
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  ExtensionComponentOverrideRenderer: (props: OverrideRendererCall) => {
    overrideRendererCalls.push(props);
    if (renderDefaultMedia.current) return props.renderDefault();
    return (
      <div
        data-testid="hero-media-override"
        data-image-url={String(props.componentProps.imageUrl ?? "")}
      >
        Extension hero media
      </div>
    );
  },
}));

import { EntityHeroLayout } from "../components/EntityHeroLayout";

type PhaseTwoEntityHeroLayoutProps = ComponentProps<typeof EntityHeroLayout> & {
  entityType: string;
  entityId: number;
  imageFit?: "cover" | "contain";
};

const PhaseTwoEntityHeroLayout = EntityHeroLayout as ComponentType<PhaseTwoEntityHeroLayoutProps>;

describe("EntityHeroLayout entity media integration", () => {
  beforeEach(() => {
    overrideRendererCalls.length = 0;
    renderDefaultMedia.current = false;
  });

  it("hides a fallback again when a replacement image loads after an error", () => {
    renderDefaultMedia.current = true;
    const props: ComponentProps<typeof EntityHeroLayout> = {
      entityType: "performer",
      entityId: 41,
      backLabel: "Back to performers",
      onGoBack: vi.fn(),
      imageUrl: "/missing-cover.jpg",
      imageAlt: "Performer cover",
      imageFallback: <span>Performer fallback</span>,
      title: "Performer",
    };
    const { container, rerender } = render(<EntityHeroLayout {...props} />);

    const missingImage = screen.getByRole("img", { name: "Performer cover" });
    const fallback = screen.getByText("Performer fallback").parentElement;
    fireEvent.error(missingImage);
    expect(missingImage).toHaveStyle({ display: "none" });
    expect(fallback).toHaveStyle({ display: "flex" });

    rerender(<EntityHeroLayout {...props} imageUrl="/replacement-cover.jpg" />);
    const replacementImage = container.querySelector<HTMLImageElement>('img[alt="Performer cover"]');
    expect(replacementImage).not.toBeNull();
    if (!replacementImage) return;
    fireEvent.load(replacementImage);
    expect(replacementImage).not.toHaveStyle({ display: "none" });
    expect(fallback).toHaveStyle({ display: "none" });
  });

  it("passes canonical hero media context while keeping the cover action host-owned", () => {
    const onImageClick = vi.fn();

    render(
      <PhaseTwoEntityHeroLayout
        entityType="tag"
        entityId={17}
        backLabel="Back to tags"
        onGoBack={vi.fn()}
        imageUrl="/tag-17.jpg"
        imageAlt="Animated Tag"
        imageFit="contain"
        imageClassName="h-full w-full object-contain p-3"
        onImageClick={onImageClick}
        imageFallback={<span>Tag fallback</span>}
        title="Animated Tag"
      />,
    );

    expect(overrideRendererCalls).toHaveLength(1);
    expect(overrideRendererCalls[0]?.targetComponent).toBe("entity.media");
    expect(overrideRendererCalls[0]?.componentProps).toEqual({
      entityType: "tag",
      entityId: 17,
      surface: "hero",
      imageUrl: "/tag-17.jpg",
      alt: "Animated Tag",
      fit: "contain",
      loading: "eager",
      className: "h-full w-full object-contain p-3",
    });

    const replacement = screen.getByTestId("hero-media-override");
    const coverAction = screen.getByTitle("Change cover");
    expect(coverAction).toContainElement(replacement);
    expect(screen.queryByRole("img", { name: "Animated Tag" })).not.toBeInTheDocument();

    fireEvent.click(coverAction);
    expect(onImageClick).toHaveBeenCalledWith("primary");
  });

  it("keeps alternate-image controls outside a non-delegating media replacement", () => {
    const onImageClick = vi.fn();

    render(
      <PhaseTwoEntityHeroLayout
        entityType="group"
        entityId={29}
        backLabel="Back to groups"
        onGoBack={vi.fn()}
        imageUrl="/group-front.jpg"
        imageAlt="Compilation front cover"
        alternateImageUrl="/group-back.jpg"
        alternateImageAlt="Compilation back cover"
        primaryImageLabel="front cover"
        alternateImageLabel="back cover"
        imageFit="contain"
        imageClassName="h-auto w-auto max-h-96 object-contain"
        onImageClick={onImageClick}
        title="Compilation"
      />,
    );

    expect(screen.getByTestId("hero-media-override")).toHaveAttribute("data-image-url", "/group-front.jpg");
    expect(overrideRendererCalls.at(-1)?.componentProps).toMatchObject({
      entityType: "group",
      entityId: 29,
      surface: "hero",
      imageUrl: "/group-front.jpg",
      alt: "Compilation front cover",
      fit: "contain",
      loading: "eager",
      className: "h-auto w-auto max-h-96 object-contain",
    });

    fireEvent.click(screen.getByRole("button", { name: "Show back cover" }));

    expect(screen.getByTestId("hero-media-override")).toHaveAttribute("data-image-url", "/group-back.jpg");
    expect(overrideRendererCalls.at(-1)?.componentProps).toMatchObject({
      entityType: "group",
      entityId: 29,
      surface: "hero",
      imageUrl: "/group-back.jpg",
      alt: "Compilation back cover",
    });
    expect(screen.getByRole("button", { name: "Show front cover" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Change cover" }));
    expect(onImageClick).toHaveBeenCalledWith("alternate");
  });
});
