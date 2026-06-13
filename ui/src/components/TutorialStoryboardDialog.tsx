import { useEffect, useMemo, useState } from "react";
import { BookOpen, Check, ChevronLeft, ChevronRight, Database, ExternalLink, FolderOpen, HelpCircle, ImageIcon, LayoutGrid, Play, RefreshCw, Search, Settings, Tag, X } from "lucide-react";
import ReactMarkdown from "react-markdown";
import type { ExtensionTutorialTopic } from "../api/types";
import { normalizeManualContext, uniqueManualContexts, type TutorialOpenRequest } from "./ManualContext";

export const TUTORIAL_STORYBOARD_STORAGE_KEY = "cove-tutorial-storyboard-complete";
export const TUTORIAL_STORYBOARD_EVENT = "cove:tutorial-storyboard-open";

export type TutorialSlideMockKind = "tasks" | "feed" | "metadata" | "settings" | "videoPlayer" | "tagging" | "images" | "extension";
type ManualBoxTone = "green" | "blue" | "purple" | "orange" | "pink" | "teal";
type ManualBoxPointContent = {
  tone?: ManualBoxTone;
  text: string;
};

export type { TutorialOpenRequest } from "./ManualContext";

export interface TutorialStoryboardSlide {
  id: string;
  title: string;
  caption?: string;
  bodyMarkdown?: string;
  imageSrc?: string;
  imageAlt?: string;
  mockKind?: TutorialSlideMockKind;
  points?: string[];
  links?: { label: string; url: string }[];
  topicLinks?: { label: string; topicId: string; slideId?: string }[];
}

export interface TutorialStoryboardTopic {
  id: string;
  title: string;
  description?: string;
  pages?: string[];
  contexts?: string[];
  extensionId?: string;
  parentTopicId?: string;
  /** When "setup", this topic is an extension's setup guide (surfaced after install). */
  kind?: string;
  order: number;
  slides: TutorialStoryboardSlide[];
}

interface TutorialTopicEntry {
  topic: TutorialStoryboardTopic;
  depth: number;
}

export const builtinTutorialTopics: TutorialStoryboardTopic[] = [
  {
    id: "getting-started",
    title: "Getting Started",
    description: "Get your library indexed, then learn where everything shows up.",
    pages: ["home", "settings"],
    order: 10,
    slides: [
      {
        id: "welcome",
        title: "Welcome to Cove",
        caption: "Cove organizes your media into videos, images, performers, tags, and more so you can browse and find things fast.",
        imageSrc: "/manual/screenshots/nav-bar.png",
        imageAlt: "Cove top navigation bar with content type links",
        points: [
          "Everything starts by pointing Cove at the folders you already have",
          "Once indexed, your media shows up across the content pages in the top bar",
          "Open this manual any time from the Help button to come back to a topic",
        ],
        topicLinks: [
          { label: "How list pages work", topicId: "list-pages" },
          { label: "What the content types are for", topicId: "content-types" },
        ],
      },
      {
        id: "library-paths",
        title: "Point Cove at your folders",
        caption: "In Settings, open Library → Paths & Storage and use + Add path to add each folder you keep media in. These are the roots the scanner will read.",
        imageSrc: "/manual/screenshots/library-paths.png",
        imageAlt: "The Library Paths settings with the Add path button",
        points: [
          "[green] + Add path, add one row per content root",
          "Per path, you can exclude videos, images, audio, or text if a folder only holds one kind",
          "Cove only reads these folders — your files stay where they are",
        ],
      },
      {
        id: "scan-generate",
        title: "Run Scan, then Generate",
        caption: "Scan finds your files and adds them to the library. Generate creates the previews, thumbnails, and sprites that make browsing smooth.",
        imageSrc: "/manual/screenshots/settings-scan-generate.png",
        imageAlt: "Scan and Generate controls in Settings",
        points: [
          "[green] Scan, run it first after adding a library folder",
          "[blue] Generate, run it next to build previews and thumbnails",
          "Both run in the background, so you can keep browsing while they work",
        ],
      },
      {
        id: "viewing-content",
        title: "Where your content lives",
        caption: "Each content type has its own page in the top navigation bar. Videos, images, performers, studios, tags, and groups all have a home there.",
        imageSrc: "/manual/screenshots/nav-bar.png",
        imageAlt: "Cove top navigation bar with content type links",
        points: [
          "[green] the content pages you can jump between",
          "Each page opens a list you can sort, filter, and switch views on",
          "Click any item to open its detail page with playback and metadata",
        ],
        topicLinks: [
          { label: "How list pages work", topicId: "list-pages" },
        ],
      },
      {
        id: "whats-next",
        title: "Where to go next",
        caption: "Once your library is in, these are the areas most people explore first.",
        imageSrc: "/manual/screenshots/nav-bar.png",
        imageAlt: "Cove navigation bar to explore content pages next",
        points: [
          "Pull in titles, performers, and tags automatically with metadata collection",
          "Add downloaders to bring new media into Cove",
          "Learn the list pages once and every content page feels familiar",
        ],
        topicLinks: [
          { label: "Metadata collection", topicId: "metadata" },
          { label: "Downloaders", topicId: "downloaders" },
          { label: "List pages", topicId: "list-pages" },
        ],
      },
    ],
  },
  {
    id: "list-pages",
    title: "List Pages",
    description: "Sorting, filtering, views, and the controls every content page shares.",
    pages: ["videos", "images", "galleries", "performers", "studios", "audios", "texts"],
    order: 20,
    slides: [
      {
        id: "anatomy",
        title: "One list page, learned everywhere",
        caption: "Every content page works the same way. Once you know one, you know them all. The toolbar above the results holds every control you need.",
        imageSrc: "/manual/screenshots/list-page-anatomy.png",
        imageAlt: "A content list page with the toolbar controls highlighted",
        points: [
          "[green] the view switcher for grid, wall, feed, and other layouts",
          "[blue] sort order and direction",
          "[purple] filters, including saved filters you can reuse",
          "[orange] page size, with an infinite option for no pages (social media style)",
          "[pink] card size, to fit more or fewer items on screen",
          "[teal] create a new item from this page",
        ],
        topicLinks: [
          { label: "What each content type is for", topicId: "content-types" },
          { label: "Searching within a page", topicId: "search" },
        ],
      },
      {
        id: "cards",
        title: "Get more from each card",
        caption: "Cards do more than they show at a glance. A few actions are worth knowing about right away.",
        imageSrc: "/manual/screenshots/card-options.png",
        imageAlt: "A single card with its menu open and linked chips highlighted",
        points: [
          "[green] the card menu, with Save for Later and Quick View",
          "Save for Later drops an item into a built-in group to revisit",
          "Quick View opens an item without leaving the list",
          "[blue] hover performers, tags, or groups on a card and click to open them",
        ],
      },
    ],
  },
  {
    id: "content-types",
    title: "Content Types",
    description: "What videos, images, performers, tags, and groups are for.",
    order: 30,
    slides: [
      {
        id: "media",
        title: "Your media",
        caption: "These are the things you actually watch, view, listen to, or read.",
        imageSrc: "/manual/screenshots/nav-bar.png",
        imageAlt: "Top navigation bar showing the content type pages",
        points: [
          "Videos are videos, with playback, a timeline, and rich metadata",
          "Images and galleries hold single pictures and collections of them",
          "Audio and text cover everything else you want to keep and organize",
        ],
        topicLinks: [
          { label: "Watching and browsing videos", topicId: "special-views" },
          { label: "Segments inside a video", topicId: "segments" },
        ],
      },
      {
        id: "people-labels",
        title: "People, studios, and labels",
        caption: "These connect your media together so you can find related items quickly.",
        imageSrc: "/manual/screenshots/people-labels.png",
        imageAlt: "A video detail page showing its studio, tags, performers, and groups",
        points: [
          "[blue] performers are the people in your media, with their own pages and filters",
          "[green] studios are the sources your media came from",
          "[purple] tags label anything you want to find again",
          "[orange] groups gather items into collections",
        ],
        topicLinks: [
          { label: "Groups and dynamic groups", topicId: "groups" },
          { label: "Tagging in depth", topicId: "tagging" },
        ],
      },
    ],
  },
  {
    id: "downloaders",
    title: "Downloaders",
    description: "Add media to Cove with downloader extensions.",
    pages: ["downloads", "downloaders"],
    order: 40,
    slides: [
      {
        id: "downloaders-what",
        title: "What downloaders do",
        caption: "Downloaders are extensions that bring new media into your library from supported sources.",
        imageSrc: "/manual/screenshots/downloaders-discover.png",
        imageAlt: "Discover page showing downloader extensions you can add",
        points: [
          "Each downloader knows how to fetch from a specific kind of source",
          "Downloaded items land in your library like any other video or image",
          "You add the downloaders you want, so Cove only does what you need",
        ],
      },
      {
        id: "downloaders-get",
        title: "Get and use a downloader",
        caption: "Downloaders come from the same place as other extensions. Install one, then use it from the download flow.",
        imageSrc: "/manual/screenshots/downloaders-discover.png",
        imageAlt: "Discover page with a downloader extension highlighted",
        points: [
          "[green] find downloaders in Discover and install the ones you want",
          "[blue] start a download and pick the installed downloader to use",
          "Downloads run in the background and appear when they finish",
        ],
        topicLinks: [
          { label: "Browsing and installing extensions", topicId: "extensions" },
        ],
      },
    ],
  },
  {
    id: "metadata",
    title: "Metadata Collection",
    description: "Fill in titles, performers, tags, and more, automatically.",
    order: 50,
    slides: [
      {
        id: "metadata-overview",
        title: "Let Cove fill in the details",
        caption: "Metadata collection pulls in titles, performers, studios, tags, and images so you spend less time typing and more time browsing.",
        imageSrc: "/manual/screenshots/tagger-view.png",
        imageAlt: "Metadata collection tools reviewing item details",
        points: [
          "Scrapers read details from a source and suggest them for an item",
          "Metadata servers can match and enrich many items at once",
          "Field provenance shows where each value came from",
        ],
        topicLinks: [
          { label: "Scrapers", topicId: "metadata-scrapers" },
          { label: "Metadata servers", topicId: "metadata-servers" },
          { label: "The tagger view", topicId: "metadata-tagger" },
          { label: "Field provenance", topicId: "metadata-provenance" },
        ],
      },
    ],
  },
  {
    id: "metadata-scrapers",
    title: "Scrapers",
    description: "Pull details for a single item from a source.",
    parentTopicId: "metadata",
    order: 51,
    slides: [
      {
        id: "scrapers-run",
        title: "Scrape one item first",
        caption: "Scrapers read an item's details from a source so you can review and apply them. Start with one item before doing many.",
        imageSrc: "/manual/screenshots/scraper-run.png",
        imageAlt: "A scrape result with fields ready to review",
        points: [
          "[green] start a scrape from an item's detail page",
          "[blue] review the suggested fields before you apply them",
          "Apply only the fields you want, then scale up once it looks right",
        ],
      },
    ],
  },
  {
    id: "metadata-servers",
    title: "Metadata Servers",
    description: "Match and enrich many items at once.",
    parentTopicId: "metadata",
    order: 52,
    slides: [
      {
        id: "servers-overview",
        title: "Enrich at scale",
        caption: "A metadata server can identify items and return rich details across your library, which is handy once a single scrape looks good.",
        imageSrc: "/manual/screenshots/tagger-view.png",
        imageAlt: "Reviewing enriched metadata across many items",
        points: [
          "Configure a server once in Settings, then reuse it everywhere",
          "Identify matches an item to a known entry and fills it in",
          "Use it for batches after you trust the results on a few items",
        ],
        topicLinks: [
          { label: "Scrapers", topicId: "metadata-scrapers" },
        ],
      },
    ],
  },
  {
    id: "metadata-tagger",
    title: "Tagger View",
    description: "A fast loop for cleaning metadata across many items.",
    parentTopicId: "metadata",
    order: 53,
    slides: [
      {
        id: "tagger-view",
        title: "Clean metadata in a loop",
        caption: "The tagger view is built for making the same kind of decision across many items quickly.",
        imageSrc: "/manual/screenshots/tagger-view.png",
        imageAlt: "The tagger view with items lined up for review",
        points: [
          "[green] the item you are reviewing, with its suggested matches",
          "[blue] apply or skip, then move straight to the next item",
          "Filter first so you only see the items you want to work through",
        ],
      },
    ],
  },
  {
    id: "metadata-provenance",
    title: "Field Provenance",
    description: "See where each piece of metadata came from.",
    parentTopicId: "metadata",
    order: 54,
    slides: [
      {
        id: "provenance",
        title: "Know where a value came from",
        caption: "Field provenance shows the source of a value so you can trust it or replace it with confidence.",
        imageSrc: "/manual/screenshots/field-provenance.png",
        imageAlt: "A field with its provenance details shown on hover",
        points: [
          "[green] hover a field to see where its value came from",
          "Sources can be a scrape, a server, or your own manual edit",
          "Use this when two sources disagree and you need to choose",
        ],
      },
    ],
  },
  {
    id: "tagging",
    title: "Tagging",
    description: "Label content consistently so browsing and filtering stay useful.",
    pages: ["tags", "tag"],
    order: 60,
    slides: [
      {
        id: "tagging-basics",
        title: "Tags describe what something is",
        caption: "Tags are reusable labels. Add them to videos, images, galleries, performers, groups, and other content so related things stay easy to find.",
        imageSrc: "/manual/screenshots/tagging-basics.png",
        imageAlt: "A tag detail page showing aliases and related content types",
        points: [
          "[green] use tags for genres, qualities, sources, themes, or any label you want to search later",
          "[blue] tags can have aliases and relationships, so one idea can still be found by several names",
          "The tag graph helps you spot related tags and clean up overlaps over time",
        ],
        topicLinks: [
          { label: "The tag graph", topicId: "special-views", slideId: "tags-graph" },
          { label: "The tagger view", topicId: "metadata-tagger" },
        ],
      },
      {
        id: "occurrence-tagging",
        title: "Tag when something appears",
        caption: "Some tags are about a whole item. Others are about where a performer, face, tag, or other thing appears inside a video.",
        imageSrc: "/manual/screenshots/occurrence-tagging.png",
        imageAlt: "A video edit page showing whole-video tags, performer occurrence tags, and the timeline overlay",
        points: [
          "[green] use normal tags when the whole item should carry the label",
          "[blue] use occurrence tagging when timing matters and you want to know where something appears",
          "[purple] occurrence tagging works with segments, which also power player bars, filters, and compilations",
        ],
        topicLinks: [
          { label: "Segments and time ranges", topicId: "segments" },
          { label: "Raw and derived segments", topicId: "segments-raw-derived" },
        ],
      },
    ],
  },
  {
    id: "segments",
    title: "Segments",
    description: "Track meaningful time ranges inside videos and reuse them.",
    pages: ["segments", "segment"],
    order: 70,
    slides: [
      {
        id: "segments-overview",
        title: "Video moments become reusable",
        caption: "Segments are time ranges inside a video. They let Cove show when tags, performers, faces, and other entities are present, then reuse those ranges in playback and organization.",
        imageSrc: "/manual/screenshots/segments-derived.png",
        imageAlt: "Segments marked along a video timeline",
        points: [
          "Use segments to see where tags, performers, faces, and other entities appear in a video",
          "Watch dedicated parts of videos, build compilations, or turn a segment into a sub-video",
          "Derived segments are usually the ones you browse, play, filter, and add to compilations",
        ],
        topicLinks: [
          { label: "Raw and derived segments", topicId: "segments-raw-derived" },
          { label: "Display profiles", topicId: "segments-display-profiles" },
          { label: "Building compilations", topicId: "segments-compilations" },
        ],
      },
    ],
  },
  {
    id: "segments-raw-derived",
    title: "Raw vs Derived",
    description: "The two kinds of segments and when each appears.",
    parentTopicId: "segments",
    order: 71,
    slides: [
      {
        id: "raw-derived",
        title: "Raw and derived segments",
        caption: "Raw segments are the marks recorded directly on a video. Derived segments are Cove's calculated results from raw marks, tags, performers, faces, and display settings.",
        imageSrc: "/manual/screenshots/segments-derived.png",
        imageAlt: "A video timeline showing raw and derived segments",
        points: [
          "[green] raw segments, the original marks on the timeline",
          "[blue] derived segments, the ranges most useful for player bars, filters, and compilations",
          "You usually inspect raw segments for source detail and use derived segments for actual browsing",
        ],
      },
    ],
  },
  {
    id: "segments-display-profiles",
    title: "Display Profiles",
    description: "Control how derived segments are shaped.",
    parentTopicId: "segments",
    order: 72,
    slides: [
      {
        id: "display-profiles",
        title: "Shape your results with profiles",
        caption: "A display profile is a saved set of rules for turning raw segments into the derived ones you see. Switch profiles to get different views of the same videos.",
        imageSrc: "/manual/screenshots/display-profiles.png",
        imageAlt: "Display profile selector with derived results",
        points: [
          "[green] pick a display profile to change how results are built",
          "Different profiles suit different ways of browsing the same library",
          "Profiles are saved, so you can reuse a setup you like",
        ],
      },
    ],
  },
  {
    id: "segments-compilations",
    title: "Compilations",
    description: "Turn grouped content into a playable sequence.",
    parentTopicId: "segments",
    order: 73,
    slides: [
      {
        id: "compilations",
        title: "Build a compilation from a group",
        caption: "A compilation plays grouped content back to back. Segments are especially powerful here, but compilations can include any supported content type.",
        imageSrc: "/manual/screenshots/compilation-play.png",
        imageAlt: "A compilation playing content from a group",
        points: [
          "[green] a group of content ready to play as a compilation",
          "[blue] the compilation player moving from one item to the next",
          "Use segments for precise video excerpts, or mix in other content when the group calls for it",
        ],
        topicLinks: [
          { label: "Groups and dynamic groups", topicId: "groups" },
        ],
      },
    ],
  },
  {
    id: "search",
    title: "Search",
    description: "Find items fast from a page or across everything.",
    pages: ["search"],
    order: 80,
    slides: [
      {
        id: "page-search",
        title: "Search within a page",
        caption: "The search bar on each content page looks across more than just the title.",
        imageSrc: "/manual/screenshots/search-bar.png",
        imageAlt: "A page search bar with results",
        points: [
          "[green] the page search bar, scoped to the current content type",
          "It matches titles, tags and their aliases, and the description",
          "It also matches performers and their aliases, and the studio",
        ],
      },
      {
        id: "global-search",
        title: "Search across everything",
        caption: "When you are not sure where something lives, the global search looks across content types at once.",
        imageSrc: "/manual/screenshots/global-search.png",
        imageAlt: "Global search with grouped results",
        points: [
          "[green] open global search to look everywhere at once",
          "Results are grouped by content type so you can jump straight in",
          "Use it as a fast way to reach any item without browsing first",
        ],
      },
    ],
  },
  {
    id: "groups",
    title: "Groups",
    description: "Collections you build by hand or that update themselves.",
    pages: ["groups", "group"],
    order: 90,
    slides: [
      {
        id: "groups-basics",
        title: "Gather items into groups",
        caption: "A group is a collection of items. You can build one by hand and add anything you like to it.",
        imageSrc: "/manual/screenshots/group-detail.png",
        imageAlt: "A group detail page with its items",
        points: [
          "[green] the items collected inside the group",
          "Add videos, images, segments, and more to the same group",
          "Groups can even contain other groups for deeper organizing",
        ],
      },
      {
        id: "dynamic-builtin",
        title: "Dynamic and built-in groups",
        caption: "Some groups fill themselves based on a filter, and Cove ships with a few that track your activity automatically.",
        imageSrc: "/manual/screenshots/dynamic-groups.png",
        imageAlt: "Dynamic groups including the built-in ones",
        points: [
          "[green] dynamic groups that update from a saved filter",
          "[blue] the built-in Watch History, Continue Watching, and Save for Later",
          "The built-in groups are managed by Cove and cannot be deleted",
        ],
        topicLinks: [
          { label: "Building compilations", topicId: "segments-compilations" },
        ],
      },
    ],
  },
  {
    id: "special-views",
    title: "Special Views",
    description: "Feed, vertical view, and the tag graph.",
    pages: ["feed", "graph"],
    order: 100,
    slides: [
      {
        id: "feed-vertical",
        title: "Feed and vertical view",
        caption: "Feed and vertical views are great for social media style browsing",
        imageSrc: "/manual/screenshots/feed-view.png",
        imageAlt: "Feed view with a scrolling session",
        points: [
          "[green] switch a list into feed or vertical view",
          "Each item plays in place as you scroll through the session",
          "Pair it with infinite page size for an uninterrupted run",
        ],
      },
      {
        id: "tags-graph",
        title: "The tag graph",
        caption: "The tag graph shows how your tags relate to each other, which helps you understand and tidy up your labels.",
        imageSrc: "/manual/screenshots/tags-graph.png",
        imageAlt: "The tag graph view",
        points: [
          "[green] the graph of tags and how they connect",
          "Use it to spot related tags and clean up overlaps",
          "Click a tag in the graph to jump straight to it",
        ],
      },
    ],
  },
  {
    id: "security",
    title: "Security",
    description: "Users, roles, permissions, content rules, and share links.",
    pages: ["users", "user"],
    order: 110,
    slides: [
      {
        id: "security-overview",
        title: "Control access deliberately",
        caption: "Security settings decide who can sign in, what actions they can take, what content they can see, and what can be shared outside an account.",
        imageSrc: "/manual/screenshots/security-overview.png",
        imageAlt: "Security settings with the Security and Access menu expanded and the users page visible",
        points: [
          "[green] users, roles, content rules, and share links live together in Security and Access",
          "[blue] each page controls a different layer of access",
          "Use them together: users sign in, roles grant actions, content rules limit visibility, and share links grant scoped temporary access",
        ],
        topicLinks: [
          { label: "Users", topicId: "security-users" },
          { label: "Roles and permissions", topicId: "security-roles-permissions" },
          { label: "Content rules", topicId: "security-content-rules" },
          { label: "Share links", topicId: "security-sharing" },
        ],
      },
    ],
  },
  {
    id: "security-users",
    title: "Users",
    description: "Create accounts and assign access.",
    parentTopicId: "security",
    pages: ["users", "user"],
    order: 111,
    slides: [
      {
        id: "users",
        title: "Give each person an account",
        caption: "User accounts let people sign in separately, keep their own activity, and receive the role that fits how they should use Cove.",
        imageSrc: "/manual/screenshots/users-admin.png",
        imageAlt: "User management with accounts and roles",
        points: [
          "[green] the list of users and their assigned roles",
          "Create accounts for people who should sign in directly",
          "Change a user's role when their access needs to change",
        ],
        topicLinks: [
          { label: "Roles and permissions", topicId: "security-roles-permissions" },
        ],
      },
    ],
  },
  {
    id: "security-roles-permissions",
    title: "Roles and Permissions",
    description: "Choose what each type of user can do.",
    parentTopicId: "security",
    order: 112,
    slides: [
      {
        id: "roles-permissions",
        title: "Roles bundle permissions",
        caption: "A role is the set of permissions a user receives. Use roles to separate everyday browsing from administrative actions.",
        imageSrc: "/manual/screenshots/roles-permissions.png",
        imageAlt: "Roles list with a role permission panel open",
        points: [
          "[green] permissions cover actions like managing settings, users, metadata, downloads, and library content",
          "[blue] open a role to review exactly what it can do",
          "Give users the smallest role that still lets them do their work",
          "Pair roles with content rules when users should only see part of the library",
        ],
        topicLinks: [
          { label: "Content rules", topicId: "security-content-rules" },
        ],
      },
    ],
  },
  {
    id: "security-content-rules",
    title: "Content Rules",
    description: "Limit which library items a role can see.",
    parentTopicId: "security",
    order: 113,
    slides: [
      {
        id: "content-rules",
        title: "Rules shape visibility",
        caption: "Content rules decide which items are visible to a role. They are useful when an account should browse only a specific part of the library.",
        imageSrc: "/manual/screenshots/content-rules.png",
        imageAlt: "Content rules settings with the create rule panel open",
        points: [
          "[green] use content rules to allow or hide content by the criteria Cove supports",
          "[blue] review saved rules and entity overrides in one place",
          "Rules work alongside permissions: one controls visibility, the other controls actions",
          "Review rules carefully before assigning them to a role used by other people",
        ],
        topicLinks: [
          { label: "Users", topicId: "security-users" },
          { label: "Share links", topicId: "security-sharing" },
        ],
      },
    ],
  },
  {
    id: "security-sharing",
    title: "Share Links",
    description: "Share selected content without creating an account.",
    parentTopicId: "security",
    order: 114,
    slides: [
      {
        id: "share-links",
        title: "Share selected content",
        caption: "Share links let you hand out access to specific content without giving someone a full account.",
        imageSrc: "/manual/screenshots/share-links.png",
        imageAlt: "Creating a share link",
        points: [
          "[green] create a share link for the content you choose",
          "Send the link to give someone scoped access",
          "Manage or revoke links from the same place later",
        ],
      },
    ],
  },
  {
    id: "appearance",
    title: "Themes and Layout",
    description: "Make Cove look how you want.",
    order: 120,
    slides: [
      {
        id: "color-palette",
        title: "Choose a color palette",
        caption: "Color palettes set Cove's core colors, including the background, surfaces, accent color, text, borders, and navigation.",
        imageSrc: "/manual/screenshots/theme-picker.png",
        imageAlt: "Appearance settings showing the color palette controls",
        points: [
          "[green] choose a palette to set the overall color system",
          "Changes apply right away so you can try a few quickly",
          "Use palettes as the foundation before fine-tuning style and layout",
        ],
      },
      {
        id: "style-layout",
        title: "Choose style and layout",
        caption: "Style options change the feel of surfaces and controls. Layout options change how pages are arranged for browsing.",
        imageSrc: "/manual/screenshots/style-layout-options.png",
        imageAlt: "Appearance settings showing style and layout options",
        points: [
          "[green] pick a style and adjust its extra options when they appear",
          "[blue] choose the layout that matches how you like to browse",
          "Combine a palette, style, and layout into a setup that feels right",
        ],
      },
    ],
  },
  {
    id: "extensions",
    title: "Extensions",
    description: "Discover and install add-ons, including downloaders.",
    pages: ["extensions", "registry", "discover"],
    order: 130,
    slides: [
      {
        id: "discover-install",
        title: "Discover and install extensions",
        caption: "Extensions add new abilities to Cove, from downloaders to scrapers to whole new panels. You browse and install them from Discover.",
        imageSrc: "/manual/screenshots/extensions-discover.png",
        imageAlt: "The Discover page listing available extensions",
        points: [
          "[green] browse available extensions in Discover",
          "[blue] install the ones you want with a click",
          "Installed extensions can add their own panels and manual topics",
        ],
        topicLinks: [
          { label: "Downloaders", topicId: "downloaders" },
          { label: "Metadata collection", topicId: "metadata" },
        ],
      },
    ],
  },
  {
    id: "content-types-people",
    title: "Performers and Studios",
    description: "The detail pages for the people and studios in your library.",
    pages: ["performers", "performer", "studios", "studio"],
    parentTopicId: "content-types",
    order: 31,
    slides: [
      {
        id: "performer-page",
        title: "A performer's page",
        caption: "A performer page gathers everything they appear in. Tabs across the top split their content by type — Videos, Galleries, Images, Audios, Texts, Groups — plus Appears With and Similar.",
        imageSrc: "/manual/screenshots/performer-page.png",
        imageAlt: "A performer detail page with content tabs",
        points: [
          "[green] the content tabs, each with a count",
          "[blue] Appears With shows performers who share content with this one",
          "Similar suggests performers you might also want",
        ],
      },
      {
        id: "studio-page",
        title: "A studio's page",
        caption: "A studio page works the same way, with one extra: Sub-studios. Studios can be nested, so a parent studio can show the studios beneath it.",
        imageSrc: "/manual/screenshots/studio-page.png",
        imageAlt: "A studio detail page showing the Sub-studios tab",
        points: [
          "[green] content tabs by type, like performers",
          "[blue] Sub-studios, the studios nested under this one",
        ],
      },
    ],
  },
  {
    id: "content-types-viewers",
    title: "Viewing each media type",
    description: "How images, galleries, audio, and text open in Cove.",
    pages: ["images", "image", "galleries", "gallery", "audios", "audio", "texts", "text"],
    parentTopicId: "content-types",
    order: 32,
    slides: [
      {
        id: "images-galleries",
        title: "Images and galleries",
        caption: "Open an image to view it full screen in the lightbox, with zoom and prev/next. A gallery groups images together — its Images tab shows the grid, and you can open any one in the same lightbox.",
        imageSrc: "/manual/screenshots/image-lightbox.png",
        imageAlt: "An image open in the lightbox viewer",
        points: [
          "[green] the lightbox, with zoom and next/previous",
          "[blue] a gallery's Images tab holds its grid",
        ],
      },
      {
        id: "audio-text",
        title: "Audio and text",
        caption: "Audio opens in a player with a scrubber and volume, and lists its tracks. Text and PDFs open in a reader — PDFs get page controls so you can jump through the document.",
        imageSrc: "/manual/screenshots/audio-text.png",
        imageAlt: "An audio detail page with the player and tracks",
        points: [
          "[green] the audio player and its Tracks tab",
          "[blue] the text/PDF reader with page controls",
        ],
      },
    ],
  },
  {
    id: "detail-pages",
    title: "Detail Pages",
    description: "What's on a video's page: playback, metadata, ratings, and editing.",
    pages: ["video", "image", "audio", "text", "gallery"],
    order: 35,
    slides: [
      {
        id: "scene-anatomy",
        title: "Anatomy of a video page",
        caption: "A video page puts the player up top and everything Cove knows about the video below, split into tabs: Details, Segments, Similar, Audio Similar, Filters, File Info, History, and Edit.",
        imageSrc: "/manual/screenshots/video-detail.png",
        imageAlt: "A video detail page showing the player and the row of tabs",
        points: [
          "[green] the player at the top",
          "[blue] the tabs that organize everything else",
          "[purple] the Details tab: studio, performers, tags, groups, galleries, and faces",
        ],
        topicLinks: [
          { label: "Tags and people", topicId: "content-types" },
          { label: "Segments", topicId: "segments" },
        ],
      },
      {
        id: "ratings-actions",
        title: "Rate, favorite, and organize",
        caption: "Alongside the video you can set a star rating, mark it a favorite, and flag it Organized once you've finished tidying its metadata. Cove also tracks likes and how often you've opened it.",
        imageSrc: "/manual/screenshots/video-detail-actions.png",
        imageAlt: "The rating, favorite, and organized controls on a video page",
        points: [
          "[green] the 5-star rating",
          "[blue] the favorite heart",
          "[orange] Organized, your 'this one's done' marker",
        ],
      },
      {
        id: "edit-metadata",
        title: "Edit the details",
        caption: "The Edit tab turns the metadata into a form so you can fix titles, dates, and descriptions or adjust tags, performers, and groups by hand.",
        imageSrc: "/manual/screenshots/video-edit.png",
        imageAlt: "The Edit tab of a video showing the metadata form",
        points: [
          "Editing needs write permission on that content type",
          "Manual edits are recorded in provenance, so you can always see what you changed",
        ],
        topicLinks: [
          { label: "Field provenance", topicId: "metadata-provenance" },
        ],
      },
    ],
  },
  {
    id: "detail-pages-playback",
    title: "Playback and Shortcuts",
    description: "Player controls, the timeline, and keyboard shortcuts.",
    pages: ["video"],
    parentTopicId: "detail-pages",
    order: 36,
    slides: [
      {
        id: "player-controls",
        title: "The player and timeline",
        caption: "Play, scrub, change speed, loop a section, and go fullscreen. The scrubber isn't just a progress bar — it shows swimlanes for segments, detections, and faces so you can see what's where and jump to it.",
        imageSrc: "/manual/screenshots/video-scrubber.png",
        imageAlt: "The video player scrubber showing segment and face swimlanes",
        points: [
          "[green] the scrubber with segment and face swimlanes",
          "[blue] speed and loop controls",
          "Click a swimlane marker to jump straight to that moment",
        ],
        topicLinks: [
          { label: "How segments work", topicId: "segments" },
        ],
      },
      {
        id: "keyboard-shortcuts",
        title: "Keyboard shortcuts",
        caption: "The player is built for the keyboard. Learn a few keys and you'll move through videos far faster than with the mouse.",
        points: [
          "Space or K — play and pause",
          "Left / Right — seek 5 seconds (hold Shift for 10)",
          "Up / Down — volume; M — mute",
          "F — fullscreen",
          "0–9 — jump to that percent of the video",
        ],
      },
    ],
  },
  {
    id: "save-for-later-history",
    title: "Save for Later and History",
    description: "Bookmark things to come back to, and pick up where you left off.",
    pages: ["home", "videos", "video", "groups"],
    order: 95,
    slides: [
      {
        id: "save-for-later",
        title: "Save things for later",
        caption: "The bookmark button — Save for Later — is on cards and detail pages. Saved items collect in the built-in Save for Later group so you can find them again without hunting.",
        points: [
          "[green] the bookmark / Save for Later button",
          "Your saved items live in the built-in Save for Later group",
        ],
        topicLinks: [
          { label: "Groups", topicId: "groups" },
        ],
      },
      {
        id: "continue-watching",
        title: "Continue watching",
        caption: "Cove remembers where you stopped. The Continue Watching row on the home page brings you back to videos in progress, and your watch history is kept as its own built-in group.",
        imageSrc: "/manual/screenshots/continue-watching.png",
        imageAlt: "The Continue Watching row on the home page",
        points: [
          "[green] the Continue Watching row on Home",
          "Watch history is a built-in group you can browse any time",
        ],
      },
    ],
  },
  {
    id: "backups-upgrades",
    title: "Backups and Upgrades",
    description: "Keep your library safe and understand the update prompt.",
    pages: ["settings"],
    contexts: ["settings-tab:operations/backup-restore", "route:/settings/operations/backup-restore"],
    order: 125,
    slides: [
      {
        id: "backups",
        title: "Back up your library",
        caption: "In Settings, open Operations → Backup & Restore. Create a database backup before big changes, and you can restore from one if something goes wrong. You can also export and import metadata here.",
        imageSrc: "/manual/screenshots/backup-restore.png",
        imageAlt: "The Backup & Restore settings with create and restore controls",
        points: [
          "[green] Create Database Backup, do this before risky changes",
          "[blue] Restore Database, roll back to a saved backup",
          "Export/Import Metadata moves library data in and out",
        ],
      },
      {
        id: "upgrades",
        title: "When Cove needs to update",
        caption: "After an update, Cove may need to apply database changes before the library opens. You'll see a Database Update Required screen — it makes a backup first, then you click Run Migration.",
        points: [
          "A backup is created automatically before any migration runs",
          "[green] Run Migration, then Cove opens as usual",
        ],
      },
    ],
  },
];

export function hasCompletedTutorialStoryboard() {
  return localStorage.getItem(TUTORIAL_STORYBOARD_STORAGE_KEY) === "true";
}

export function openTutorialStoryboard(request?: TutorialOpenRequest | string) {
  const detail = typeof request === "string" ? { topicId: request } : request;
  window.dispatchEvent(new CustomEvent<TutorialOpenRequest | undefined>(TUTORIAL_STORYBOARD_EVENT, { detail }));
}

interface Props {
  open: boolean;
  onClose: () => void;
  request?: TutorialOpenRequest;
  currentPage?: string;
  extensionTopics?: ExtensionTutorialTopic[];
  onTopicChange?: (topicId: string, slideId?: string) => void;
}

export function TutorialStoryboardDialog({ open, onClose, request, currentPage, extensionTopics = [], onTopicChange }: Props) {
  const topics = useMemo(() => mergeTutorialTopics(extensionTopics), [extensionTopics]);
  const topicEntries = useMemo(() => buildTopicEntries(topics), [topics]);
  const orderedTopicIds = useMemo(() => topicEntries.map((entry) => entry.topic.id), [topicEntries]);
  const parentByChild = useMemo(() => {
    const map = new Map<string, string>();
    for (const topic of topics) {
      if (topic.parentTopicId && topics.some((candidate) => candidate.id === topic.parentTopicId)) {
        map.set(topic.id, topic.parentTopicId);
      }
    }
    return map;
  }, [topics]);
  const parentIdsWithChildren = useMemo(() => new Set(parentByChild.values()), [parentByChild]);

  const [selectedTopicId, setSelectedTopicId] = useState(() => pickInitialTopicId(topics, request, currentPage));
  const [index, setIndex] = useState(0);
  const [search, setSearch] = useState("");
  // Nested topics start collapsed. A parent counts as expanded when its id is in this
  // set, or when it is on the path to the currently selected topic (so the active
  // branch is always visible).
  const [expandedTopicIds, setExpandedTopicIds] = useState<Set<string>>(() => new Set());

  const selectedTopic = topics.find((topic) => topic.id === selectedTopicId) ?? topics[0];
  const slide = selectedTopic.slides[index] ?? selectedTopic.slides[0];
  const isLast = index === selectedTopic.slides.length - 1;
  const progressLabel = `${index + 1} of ${selectedTopic.slides.length}`;
  const currentOrderIndex = orderedTopicIds.indexOf(selectedTopic.id);
  const isVeryFirst = currentOrderIndex <= 0 && index === 0;
  const isVeryLast = currentOrderIndex === orderedTopicIds.length - 1 && isLast;

  const selectedAncestors = useMemo(
    () => new Set([selectedTopic.id, ...ancestorsOf(selectedTopic.id, parentByChild)]),
    [selectedTopic.id, parentByChild],
  );
  const isTopicOpen = (topicId: string) => expandedTopicIds.has(topicId) || selectedAncestors.has(topicId);
  const trimmedSearch = search.trim();
  const visibleEntries = trimmedSearch
    ? topicEntries.filter(({ topic }) => matchesTopicSearch(topic, trimmedSearch))
    : topicEntries.filter(({ topic }) => ancestorsOf(topic.id, parentByChild).every((ancestorId) => isTopicOpen(ancestorId)));

  useEffect(() => {
    if (!open) return;
    const nextTopicId = pickInitialTopicId(topics, request, currentPage);
    const nextTopic = topics.find((topic) => topic.id === nextTopicId) ?? topics[0];
    const nextSlideIndex = request?.slideId ? Math.max(0, nextTopic.slides.findIndex((item) => item.id === request.slideId)) : 0;
    setSelectedTopicId(nextTopic.id);
    setIndex(nextSlideIndex);
    setSearch("");
  }, [currentPage, open, request, topics]);

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT")) return;
      if (event.key === "Escape") {
        markCompleteAndClose();
      } else if (event.key === "ArrowRight") {
        goToNext();
      } else if (event.key === "ArrowLeft") {
        goToPrevious();
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, selectedTopic.id, index, isLast, currentOrderIndex, orderedTopicIds]);

  if (!open || !selectedTopic || !slide) return null;

  function markCompleteAndClose() {
    localStorage.setItem(TUTORIAL_STORYBOARD_STORAGE_KEY, "true");
    onClose();
  }

  function chooseTopic(topicId: string, slideId?: string) {
    const target = topics.find((topic) => topic.id === topicId);
    const slideIndex = slideId && target ? Math.max(0, target.slides.findIndex((item) => item.id === slideId)) : 0;
    setSelectedTopicId(topicId);
    setIndex(slideIndex);
    if (parentIdsWithChildren.has(topicId)) {
      setExpandedTopicIds((current) => (current.has(topicId) ? current : new Set(current).add(topicId)));
    }
    onTopicChange?.(topicId, slideId);
  }

  function toggleTopicCollapse(topicId: string) {
    setExpandedTopicIds((current) => {
      const next = new Set(current);
      if (next.has(topicId)) next.delete(topicId);
      else next.add(topicId);
      return next;
    });
  }

  function goToNext() {
    if (!isLast) {
      setIndex((current) => Math.min(selectedTopic.slides.length - 1, current + 1));
      return;
    }
    const nextTopicId = orderedTopicIds[currentOrderIndex + 1];
    if (!nextTopicId) {
      markCompleteAndClose();
      return;
    }
    chooseTopic(nextTopicId);
  }

  function goToPrevious() {
    if (index > 0) {
      setIndex((current) => Math.max(0, current - 1));
      return;
    }
    const previousTopicId = orderedTopicIds[currentOrderIndex - 1];
    if (!previousTopicId) return;
    const previousTopic = topics.find((topic) => topic.id === previousTopicId);
    const lastSlideId = previousTopic?.slides[previousTopic.slides.length - 1]?.id;
    chooseTopic(previousTopicId, lastSlideId);
  }

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center bg-black/70 px-3 py-4" role="dialog" aria-modal="true" aria-labelledby="tutorial-storyboard-title">
      <div className="flex h-[90vh] max-h-[92vh] w-[96vw] max-w-[96rem] flex-col overflow-hidden rounded-xl border border-border bg-background shadow-2xl">
        <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
          <div className="flex min-w-0 items-center gap-3">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-accent/15 text-accent">
              <BookOpen className="h-5 w-5" />
            </div>
            <div className="min-w-0">
              <div className="text-xs font-semibold uppercase tracking-wide text-muted">Cove manual</div>
              <h2 id="tutorial-storyboard-title" className="truncate text-base font-semibold text-foreground">{selectedTopic.title}</h2>
            </div>
          </div>
          <button type="button" onClick={markCompleteAndClose} className="rounded p-2 text-muted transition-colors hover:bg-surface hover:text-foreground" title="Close manual">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid min-h-0 flex-1 overflow-hidden lg:grid-cols-[17rem_minmax(0,1.45fr)_minmax(18rem,0.55fr)]">
          <aside className="hidden min-h-0 flex-col border-r border-border bg-nav/40 p-3 lg:flex">
            <div className="mb-2 px-2 text-xs font-semibold uppercase tracking-wide text-muted">Topics</div>
            <div className="relative mb-2">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Search topics"
                aria-label="Search manual topics"
                className="w-full rounded-lg border border-border bg-input py-1.5 pl-8 pr-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
              />
            </div>
            <div className="space-y-1 overflow-y-auto pr-1">
              {visibleEntries.length === 0 ? (
                <div className="px-3 py-2 text-sm text-muted">No topics match “{trimmedSearch}”.</div>
              ) : (
                visibleEntries.map(({ topic, depth }) => {
                  const hasChildren = parentIdsWithChildren.has(topic.id);
                  const expanded = isTopicOpen(topic.id);
                  const showToggle = hasChildren && !trimmedSearch;
                  return (
                    <div key={topic.id} className="flex items-stretch gap-1" style={{ paddingLeft: `${depth * 1.1}rem` }}>
                      {showToggle ? (
                        <button
                          type="button"
                          onClick={() => toggleTopicCollapse(topic.id)}
                          className="flex w-6 shrink-0 items-center justify-center rounded-md text-muted transition-colors hover:bg-card hover:text-foreground"
                          aria-label={expanded ? `Collapse ${topic.title}` : `Expand ${topic.title}`}
                          aria-expanded={expanded}
                        >
                          <ChevronRight className={`h-4 w-4 transition-transform ${expanded ? "rotate-90" : ""}`} />
                        </button>
                      ) : (
                        <span className="w-6 shrink-0" aria-hidden="true" />
                      )}
                      <button
                        type="button"
                        onClick={() => chooseTopic(topic.id)}
                        data-topic-depth={depth}
                        className={`min-w-0 flex-1 rounded-lg px-3 py-2 text-left transition-colors ${topic.id === selectedTopic.id ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"}`}
                      >
                        <span className="block truncate text-sm font-medium">{topic.title}</span>
                        {topic.description ? <span className="mt-0.5 line-clamp-2 block text-xs text-muted">{topic.description}</span> : null}
                        {topic.extensionId ? <span className="mt-1 block text-[11px] text-muted">Extension</span> : null}
                      </button>
                    </div>
                  );
                })
              )}
            </div>
          </aside>

          <div className="min-h-0 overflow-y-auto bg-black/20 p-4 sm:p-6">
            <div className="mb-3 flex gap-1.5 lg:hidden">
              <select
                value={selectedTopic.id}
                onChange={(event) => chooseTopic(event.target.value)}
                className="w-full rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground"
                aria-label="Tutorial topic"
              >
                {topicEntries.map(({ topic, depth }) => <option key={topic.id} value={topic.id}>{`${"  ".repeat(depth)}${topic.title}`}</option>)}
              </select>
            </div>
            <StoryboardPreview slide={slide} />
          </div>

          <aside className="flex min-h-0 flex-col overflow-y-auto border-t border-border p-4 lg:border-l lg:border-t-0 sm:p-6">
            <div className="text-xs font-semibold uppercase tracking-wide text-muted">{progressLabel}</div>
            <h3 className="mt-2 text-xl font-semibold text-foreground">{slide.title}</h3>
            {slide.caption ? <p className="mt-3 text-sm leading-6 text-secondary">{slide.caption}</p> : null}
            {slide.bodyMarkdown ? <ManualMarkdown markdown={slide.bodyMarkdown} /> : null}
            {(slide.points?.length ?? 0) > 0 ? (
              <div className="mt-5 space-y-2">
                {slide.points!.map((point) => (
                  <ManualBoxPoint key={point} point={point} />
                ))}
              </div>
            ) : null}
            {(slide.links?.length ?? 0) > 0 ? (
              <div className="mt-5 space-y-2">
                {slide.links!.map((link) => (
                  <a
                    key={`${link.label}:${link.url}`}
                    href={link.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-card/70 px-3 py-2 text-sm text-accent transition-colors hover:border-accent hover:bg-card"
                  >
                    <span className="truncate">{link.label}</span>
                    <ExternalLink className="h-4 w-4 shrink-0" />
                  </a>
                ))}
              </div>
            ) : null}

            {(() => {
              const topicLinks = (slide.topicLinks ?? []).filter((link) => topics.some((topic) => topic.id === link.topicId));
              if (topicLinks.length === 0) return null;
              return (
                <div className="mt-5 space-y-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-muted">Keep going</div>
                  {topicLinks.map((link) => (
                    <button
                      key={`${link.topicId}:${link.slideId ?? ""}:${link.label}`}
                      type="button"
                      onClick={() => chooseTopic(link.topicId, link.slideId)}
                      className="inline-flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-card/70 px-3 py-2 text-sm text-accent transition-colors hover:border-accent hover:bg-card"
                    >
                      <span className="truncate">{link.label}</span>
                      <ChevronRight className="h-4 w-4 shrink-0" />
                    </button>
                  ))}
                </div>
              );
            })()}

            <div className="mt-auto pt-6">
              <div className="mb-4 flex gap-1.5">
                {selectedTopic.slides.map((item, itemIndex) => (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => setIndex(itemIndex)}
                    className={`h-1.5 flex-1 rounded-full transition-colors ${itemIndex === index ? "bg-accent" : "bg-border hover:bg-muted"}`}
                    aria-label={`Go to slide ${itemIndex + 1}`}
                  />
                ))}
              </div>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <button
                  type="button"
                  onClick={goToPrevious}
                  disabled={isVeryFirst}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-2 text-sm text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-45"
                >
                  <ChevronLeft className="h-4 w-4" />
                  Back
                </button>
                <button
                  type="button"
                  onClick={goToNext}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover"
                >
                  {isVeryLast ? "Done" : "Next"}
                  {isVeryLast ? <Check className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                </button>
              </div>
            </div>
          </aside>
        </div>
      </div>
    </div>
  );
}

function mergeTutorialTopics(extensionTopics: ExtensionTutorialTopic[]): TutorialStoryboardTopic[] {
  const normalizedExtensionTopics = extensionTopics
    .filter((topic) => topic.id && topic.title && (topic.slides?.length ?? 0) > 0)
    .map<TutorialStoryboardTopic>((topic) => ({
      id: topic.id,
      title: topic.title,
      description: topic.description,
      pages: topic.pages,
      contexts: normalizeManualContexts(topic.contexts),
      extensionId: topic.extensionId,
      parentTopicId: topic.parentTopicId,
      kind: topic.kind,
      order: topic.order ?? 100,
      slides: (topic.slides ?? []).map((slide) => ({
        id: slide.id,
        title: slide.title,
        caption: slide.caption,
        bodyMarkdown: slide.bodyMarkdown,
        imageSrc: resolveManualImageSrc(slide.imageSrc, topic.extensionId),
        imageAlt: slide.imageAlt,
        mockKind: normalizeMockKind(slide.mockKind),
        points: slide.points?.length ? slide.points : [],
        links: normalizeManualLinks(slide.links),
      })),
    }));

  return [...builtinTutorialTopics, ...normalizedExtensionTopics].sort((left, right) => left.order - right.order || left.title.localeCompare(right.title));
}

function buildTopicEntries(topics: TutorialStoryboardTopic[]): TutorialTopicEntry[] {
  const sorted = [...topics].sort((left, right) => left.order - right.order || left.title.localeCompare(right.title));
  const byId = new Map(sorted.map((topic) => [topic.id, topic]));
  const childrenByParent = new Map<string, TutorialStoryboardTopic[]>();
  const roots: TutorialStoryboardTopic[] = [];

  for (const topic of sorted) {
    if (topic.parentTopicId && byId.has(topic.parentTopicId)) {
      const children = childrenByParent.get(topic.parentTopicId) ?? [];
      children.push(topic);
      childrenByParent.set(topic.parentTopicId, children);
    } else {
      roots.push(topic);
    }
  }

  const entries: TutorialTopicEntry[] = [];
  const visited = new Set<string>();
  const visit = (topic: TutorialStoryboardTopic, depth: number) => {
    if (visited.has(topic.id)) return;
    visited.add(topic.id);
    entries.push({ topic, depth });
    for (const child of childrenByParent.get(topic.id) ?? []) {
      visit(child, depth + 1);
    }
  };

  for (const topic of roots) {
    visit(topic, 0);
  }

  for (const topic of sorted) {
    visit(topic, 0);
  }

  return entries;
}

function ancestorsOf(topicId: string, parentByChild: Map<string, string>): string[] {
  const result: string[] = [];
  let current = parentByChild.get(topicId);
  let guard = 0;
  while (current && guard++ < 32) {
    result.push(current);
    current = parentByChild.get(current);
  }
  return result;
}

function matchesTopicSearch(topic: TutorialStoryboardTopic, query: string): boolean {
  const needle = query.toLowerCase();
  if (topic.title.toLowerCase().includes(needle)) return true;
  if (topic.description?.toLowerCase().includes(needle)) return true;
  return topic.slides.some(
    (slide) =>
      slide.title.toLowerCase().includes(needle) ||
      slide.caption?.toLowerCase().includes(needle) ||
      slide.points?.some((point) => point.toLowerCase().includes(needle)),
  );
}

function normalizeManualLinks(links?: { label: string; url: string }[]) {
  return (links ?? [])
    .map((link) => ({ label: link.label?.trim(), url: normalizeManualLinkUrl(link.url) }))
    .filter((link): link is { label: string; url: string } => Boolean(link.label && link.url));
}

function normalizeManualLinkUrl(url?: string) {
  if (!url) return undefined;
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.toString() : undefined;
  } catch {
    return undefined;
  }
}

function ManualBoxPoint({ point }: { point: string }) {
  const parsed = parseManualBoxPoint(point);
  const tone = parsed.tone;
  const toneClasses = tone ? manualBoxToneClasses[tone] : "border-border bg-card/70";
  const dotClasses = tone ? manualBoxDotClasses[tone] : "bg-accent";

  return (
    <div
      data-box-tone={tone}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-sm text-secondary transition-colors ${toneClasses}`}
    >
      <span className={`mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full ${dotClasses}`} aria-hidden="true" />
      <span>{parsed.text}</span>
    </div>
  );
}

function parseManualBoxPoint(point: string): ManualBoxPointContent {
  const trimmed = point.trim();
  const explicit = trimmed.match(/^\[(green|blue|purple|orange|pink|teal)\]\s*(.*)$/i);
  if (explicit) {
    return {
      tone: explicit[1].toLowerCase() as ManualBoxTone,
      text: explicit[2].trim() || trimmed,
    };
  }

  const legacy = trimmed.match(/^(?:the\s+)?(green|blue|purple|orange|pink|teal)\s+box(?:\s+is)?[:\s-]+(.*)$/i);
  if (legacy) {
    return {
      tone: legacy[1].toLowerCase() as ManualBoxTone,
      text: legacy[2].trim() || trimmed,
    };
  }

  return { text: trimmed };
}

const manualBoxToneClasses: Record<ManualBoxTone, string> = {
  green: "border-green-500/55 hover:border-green-400/75",
  blue: "border-blue-500/55 hover:border-blue-400/75",
  purple: "border-violet-500/55 hover:border-violet-400/75",
  orange: "border-orange-500/55 hover:border-orange-400/75",
  pink: "border-pink-500/55 hover:border-pink-400/75",
  teal: "border-teal-500/55 hover:border-teal-400/75",
};

const manualBoxDotClasses: Record<ManualBoxTone, string> = {
  green: "bg-green-400",
  blue: "bg-blue-400",
  purple: "bg-violet-400",
  orange: "bg-orange-400",
  pink: "bg-pink-400",
  teal: "bg-teal-400",
};

function resolveManualImageSrc(imageSrc: string | undefined, extensionId: string | undefined) {
  const value = imageSrc?.trim();
  if (!value) return undefined;
  if (isAbsoluteManualAssetUrl(value) || value.startsWith("/")) return value;
  if (!extensionId) return value;

  const normalizedPath = value.replace(/^\.\//, "").split("/").filter(Boolean).map(encodeURIComponent).join("/");
  return `/api/extensions/assets/${encodeURIComponent(extensionId)}/${normalizedPath}`;
}

function isAbsoluteManualAssetUrl(value: string) {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:" || parsed.protocol === "data:";
  } catch {
    return false;
  }
}

function ManualMarkdown({ markdown }: { markdown: string }) {
  return (
    <div className="mt-4 text-sm leading-6 text-secondary">
      <ReactMarkdown
        components={{
          p: ({ children }) => <p className="mb-3 last:mb-0">{children}</p>,
          ul: ({ children }) => <ul className="mb-3 list-disc space-y-1 pl-5 last:mb-0">{children}</ul>,
          ol: ({ children }) => <ol className="mb-3 list-decimal space-y-1 pl-5 last:mb-0">{children}</ol>,
          li: ({ children }) => <li>{children}</li>,
          strong: ({ children }) => <strong className="font-semibold text-foreground">{children}</strong>,
          code: ({ children }) => <code className="rounded bg-card px-1 py-0.5 text-xs text-foreground">{children}</code>,
          a: ({ href, children }) => {
            const safeHref = normalizeManualLinkUrl(href);
            return safeHref ? <a href={safeHref} target="_blank" rel="noopener noreferrer" className="text-accent hover:underline">{children}</a> : <span>{children}</span>;
          },
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

function normalizeMockKind(value?: string): TutorialSlideMockKind | undefined {
  const knownKinds = new Set<TutorialSlideMockKind>(["tasks", "feed", "metadata", "settings", "videoPlayer", "tagging", "images", "extension"]);
  return knownKinds.has(value as TutorialSlideMockKind) ? value as TutorialSlideMockKind : undefined;
}

function normalizeManualContexts(contexts?: string[]) {
  return uniqueManualContexts(contexts ?? []);
}

function pickInitialTopicId(topics: TutorialStoryboardTopic[], request?: TutorialOpenRequest, currentPage?: string) {
  if (request?.topicId && topics.some((topic) => topic.id === request.topicId)) {
    return request.topicId;
  }

  const contextTopicId = pickTopicIdForContexts(topics, request, currentPage);
  if (contextTopicId) {
    return contextTopicId;
  }

  const page = request?.page ?? currentPage;
  if (page) {
    const pageTopic = topics.find((topic) => topic.pages?.includes(page));
    if (pageTopic) return pageTopic.id;
  }

  return topics.find((topic) => topic.id === "getting-started")?.id ?? topics[0]?.id ?? "getting-started";
}

function pickTopicIdForContexts(topics: TutorialStoryboardTopic[], request?: TutorialOpenRequest, currentPage?: string) {
  const contexts = uniqueManualContexts([
    request?.context,
    ...(request?.contexts ?? []),
    currentPage ? `page:${currentPage}` : undefined,
  ]);

  let bestMatch: { topicId: string; score: number } | undefined;

  topics.forEach((topic, topicIndex) => {
    contexts.forEach((context, contextIndex) => {
      const score = scoreTopicContextMatch(topic, context, contextIndex, topicIndex);
      if (score == null) return;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { topicId: topic.id, score };
      }
    });
  });

  return bestMatch?.topicId;
}

function scoreTopicContextMatch(topic: TutorialStoryboardTopic, context: string, contextIndex: number, topicIndex: number) {
  const normalizedContext = normalizeManualContext(context);
  if (!normalizedContext) return undefined;

  if (topic.contexts?.some((topicContext) => normalizeManualContext(topicContext) === normalizedContext)) {
    return 10000 - contextIndex * 10 - topicIndex / 1000;
  }

  if (normalizedContext.startsWith("page:")) {
    const page = normalizedContext.slice("page:".length);
    if (topic.pages?.some((topicPage) => topicPage.toLowerCase() === page)) {
      return 1000 - contextIndex * 10 - topicIndex / 1000;
    }
  }

  return undefined;
}

function SlideImage({ src, alt }: { src: string; alt: string }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [src]);
  const fileName = src.split("/").pop() ?? src;

  if (failed) {
    return (
      <div className="flex h-full min-h-[34rem] flex-col items-center justify-center gap-4 rounded-lg border border-dashed border-border bg-card p-10 text-center shadow-xl">
        <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-accent/10 text-accent">
          <ImageIcon className="h-8 w-8" />
        </div>
        <div className="max-w-md">
          <div className="text-sm font-semibold uppercase tracking-wide text-muted">Screenshot pending</div>
          <div className="mt-2 text-base font-medium text-foreground">{alt}</div>
          <div className="mt-2 rounded bg-background px-3 py-1.5 font-mono text-xs text-secondary">{fileName}</div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full min-h-[34rem] items-center justify-center overflow-hidden rounded-lg border border-border bg-card shadow-xl">
      <img src={src} alt={alt} onError={() => setFailed(true)} className="block max-h-full w-full object-contain bg-black" />
    </div>
  );
}

function StoryboardPreview({ slide }: { slide: TutorialStoryboardSlide }) {
  if (slide.imageSrc) {
    return <SlideImage src={slide.imageSrc} alt={slide.imageAlt ?? slide.title} />;
  }

  if (!slide.mockKind) {
    return (
      <div className="flex h-full min-h-[34rem] flex-col items-center justify-center gap-4 rounded-lg border border-border bg-card p-10 text-center shadow-xl">
        <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-accent/15 text-accent">
          <BookOpen className="h-8 w-8" />
        </div>
        <div className="max-w-md">
          <div className="text-xl font-semibold text-foreground">{slide.title}</div>
          {slide.caption ? <p className="mt-2 text-sm leading-6 text-secondary">{slide.caption}</p> : null}
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto h-full min-h-[34rem] max-w-5xl overflow-hidden rounded-lg border border-border bg-card shadow-xl">
      <div className="flex items-center gap-2 border-b border-border bg-nav px-3 py-2">
        <div className="h-2.5 w-2.5 rounded-full bg-red-400/80" />
        <div className="h-2.5 w-2.5 rounded-full bg-amber-300/80" />
        <div className="h-2.5 w-2.5 rounded-full bg-green-400/80" />
        <div className="ml-2 h-6 flex-1 rounded bg-background px-3 text-xs leading-6 text-muted">cove.local</div>
      </div>
      <div className="grid min-h-[calc(100%-2.5rem)] grid-cols-[10rem_minmax(0,1fr)] bg-background">
        <div className="border-r border-border bg-nav/90 p-3">
          <div className="mb-4 h-7 rounded bg-accent/25" />
          {["Videos", "Images", "Texts", "Settings"].map((item, index) => (
            <div key={item} className={`mb-2 flex items-center gap-2 rounded px-2 py-2 text-xs ${index === 0 ? "bg-accent/20 text-accent" : "text-secondary"}`}>
              <div className="h-3 w-3 rounded bg-current opacity-60" />
              <span>{item}</span>
            </div>
          ))}
        </div>
        <div className="p-4">
          {slide.mockKind === "tasks" ? <TasksMock /> : null}
          {slide.mockKind === "feed" ? <FeedMock /> : null}
          {slide.mockKind === "metadata" ? <MetadataMock /> : null}
          {slide.mockKind === "settings" ? <SettingsMock /> : null}
          {slide.mockKind === "videoPlayer" ? <VideoPlayerMock /> : null}
          {slide.mockKind === "tagging" ? <TaggingMock /> : null}
          {slide.mockKind === "images" ? <ImagesMock /> : null}
          {slide.mockKind === "extension" || !slide.mockKind ? <ExtensionMock /> : null}
        </div>
      </div>
    </div>
  );
}

function TasksMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-lg font-semibold text-foreground">Scan & Generate</div>
          <div className="text-xs text-muted">Library indexing</div>
        </div>
        <RefreshCw className="h-5 w-5 text-accent" />
      </div>
      {["Scan library", "Generate previews", "Build hashes"].map((label, index) => (
        <div key={label} className="rounded-lg border border-border bg-card p-3">
          <div className="flex items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <FolderOpen className="h-5 w-5 text-accent" />
              <div>
                <div className="text-sm font-medium text-foreground">{label}</div>
                <div className="text-xs text-muted">{index === 0 ? "Run first" : "Queue when ready"}</div>
              </div>
            </div>
            <div className="rounded bg-accent px-3 py-1 text-xs font-medium text-white">Run</div>
          </div>
        </div>
      ))}
    </div>
  );
}

function FeedMock() {
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2 text-xs">
        {["Grid", "Feed", "Wall", "Infinite"].map((label, index) => (
          <div key={label} className={`rounded px-2.5 py-1 ${index === 1 || index === 3 ? "bg-accent text-white" : "bg-card text-secondary"}`}>{label}</div>
        ))}
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        {[0, 1].map((item) => (
          <div key={item} className="overflow-hidden rounded-lg border border-border bg-card">
            <div className="aspect-video bg-gradient-to-br from-accent/70 via-cyan-500/35 to-rose-400/45" />
            <div className="space-y-2 p-3">
              <div className="h-2.5 w-3/4 rounded bg-foreground/75" />
              <div className="flex gap-1.5">
                <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted">tag</span>
                <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted">rating</span>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function MetadataMock() {
  return (
    <div className="grid gap-3 md:grid-cols-[1.1fr_0.9fr]">
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="aspect-[4/3] bg-gradient-to-br from-sky-500/50 via-accent/40 to-fuchsia-400/40" />
        <div className="space-y-2 p-3">
          <div className="h-2.5 w-4/5 rounded bg-foreground/80" />
          <div className="h-2.5 w-2/3 rounded bg-foreground/35" />
        </div>
      </div>
      <div className="rounded-lg border border-border bg-card p-3">
        <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-foreground"><Database className="h-4 w-4 text-accent" /> Metadata</div>
        {["Scrape", "Identify", "Apply fields"].map((label, index) => (
          <div key={label} className={`mb-2 rounded px-3 py-2 text-sm ${index === 0 ? "bg-accent text-white" : "bg-background text-secondary"}`}>{label}</div>
        ))}
      </div>
    </div>
  );
}

function SettingsMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground"><Settings className="h-5 w-5 text-accent" /> Settings</div>
      <div className="grid gap-3 md:grid-cols-2">
        {["Navigation", "Video Player", "Feed & Viewer", "Extensions"].map((label, index) => (
          <div key={label} className="rounded-lg border border-border bg-card p-3">
            <div className="mb-3 flex items-center gap-2 text-sm font-medium text-foreground">
              {index === 0 ? <LayoutGrid className="h-4 w-4 text-accent" /> : <Play className="h-4 w-4 text-accent" />}
              {label}
            </div>
            <div className="space-y-2">
              <div className="h-2 rounded bg-foreground/60" />
              <div className="h-2 w-2/3 rounded bg-foreground/30" />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function VideoPlayerMock() {
  return (
    <div className="space-y-3">
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-slate-900 via-indigo-950 to-cyan-950">
          <Play className="h-14 w-14 rounded-full bg-black/40 p-3 text-white" />
        </div>
        <div className="space-y-2 p-3">
          <div className="h-2 rounded bg-accent" />
          <div className="flex justify-between text-xs text-muted"><span>04:12</span><span>38:20</span></div>
        </div>
      </div>
      <div className="grid gap-3 md:grid-cols-3">
        {["Resume", "Segments", "Details"].map((label) => <div key={label} className="rounded-lg border border-border bg-card p-3 text-sm text-secondary">{label}</div>)}
      </div>
    </div>
  );
}

function TaggingMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground"><Tag className="h-5 w-5 text-accent" /> Tagger</div>
      <div className="grid gap-2 md:grid-cols-3">
        {Array.from({ length: 9 }, (_, index) => (
          <div key={index} className="rounded-lg border border-border bg-card p-3">
            <div className="mb-3 aspect-video rounded bg-gradient-to-br from-teal-500/35 via-accent/25 to-amber-300/30" />
            <div className="h-2 rounded bg-foreground/60" />
          </div>
        ))}
      </div>
    </div>
  );
}

function ImagesMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground"><ImageIcon className="h-5 w-5 text-accent" /> Images</div>
      <div className="columns-3 gap-2">
        {[1.1, 0.75, 1.35, 0.9, 1.25, 0.8, 1.45, 1].map((ratio, index) => (
          <div key={index} className="mb-2 break-inside-avoid overflow-hidden rounded-lg border border-border bg-card">
            <div style={{ aspectRatio: `1 / ${ratio}` }} className="bg-gradient-to-br from-emerald-500/40 via-sky-400/25 to-rose-400/35" />
          </div>
        ))}
      </div>
    </div>
  );
}

function ExtensionMock() {
  return (
    <div className="flex h-full min-h-[28rem] flex-col items-center justify-center rounded-lg border border-border bg-card p-6 text-center">
      <HelpCircle className="mb-4 h-12 w-12 text-accent" />
      <div className="text-lg font-semibold text-foreground">Extension Topic</div>
      <div className="mt-2 max-w-md text-sm leading-6 text-secondary">This slide can be supplied by a Cove extension with its own screenshots, points, and page targeting.</div>
    </div>
  );
}

