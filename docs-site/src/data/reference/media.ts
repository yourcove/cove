type NonEmpty<T> = readonly [T, ...T[]];

interface NamedMeaning {
  name: string;
  meaning: string;
}

interface Relationship {
  name: string;
  cardinality: 'Zero or one' | 'Zero or more';
  behavior: string;
}

interface Capability {
  name: string;
  support: 'Built in' | 'Supported' | 'Optional' | 'Conditional' | 'Not applicable';
  behavior: string;
}

interface Availability {
  name: string;
  requirement: string;
}

interface Lifecycle {
  operation: string;
  effect: string;
}

export interface MediaReference {
  fields: NonEmpty<NamedMeaning>;
  relationships: NonEmpty<Relationship>;
  files: NonEmpty<NamedMeaning>;
  capabilities: NonEmpty<Capability>;
  availability: NonEmpty<Availability>;
  lifecycle: NonEmpty<Lifecycle>;
}

export const MEDIA_REFERENCE_SECTIONS = [
  { key: 'fields', heading: 'Descriptive fields', columns: ['Field', 'Meaning'] },
  { key: 'relationships', heading: 'Relationships', columns: ['Relationship', 'Cardinality', 'Behavior'] },
  { key: 'files', heading: 'Files and technical values', columns: ['File value', 'Meaning'] },
  { key: 'capabilities', heading: 'Capabilities', columns: ['Capability', 'Support', 'Behavior'] },
  { key: 'availability', heading: 'Availability and requirements', columns: ['Surface or operation', 'Availability'] },
  { key: 'lifecycle', heading: 'Lifecycle and deletion', columns: ['Event or operation', 'Effect'] },
] as const satisfies readonly {
  key: keyof MediaReference;
  heading: string;
  columns: readonly string[];
}[];

export type MediaReferenceSection = typeof MEDIA_REFERENCE_SECTIONS[number]['key'];

function defineMediaReference(reference: MediaReference): MediaReference {
  return reference;
}

export const videoReference = defineMediaReference({
  fields: [
    { name: 'Title', meaning: 'Display title for the video.' },
    { name: 'Date', meaning: 'Calendar date associated with the video.' },
    { name: 'Studio Code', meaning: 'Studio- or publisher-assigned catalog code.' },
    { name: 'Director', meaning: 'Free-text director value.' },
    { name: 'Details', meaning: 'Longer free-text description.' },
    { name: 'Captions', meaning: 'Free-text metadata retained on the record; distinct from discovered sidecar caption tracks.' },
    { name: 'Organized', meaning: 'User-managed flag indicating that the record has reached the desired organization state.' },
    { name: 'VR', meaning: 'Marks the video as virtual-reality content.' },
    { name: 'Cover', meaning: 'Selected or generated image used to represent the video.' },
    { name: 'Created and Updated', meaning: 'Timestamps maintained by Cove for the record.' },
  ],
  relationships: [
    { name: 'Studio', cardinality: 'Zero or one', behavior: 'Primary studio associated with the video.' },
    { name: 'Tags', cardinality: 'Zero or more', behavior: 'Whole-video tags; contextual applications can instead describe a performer occurrence or time range.' },
    { name: 'Performers', cardinality: 'Zero or more', behavior: 'Associated performer identities; dated videos can display age at the video date.' },
    { name: 'Groups', cardinality: 'Zero or more', behavior: 'Membership can carry a Video # position used by ordered groups.' },
    { name: 'Galleries', cardinality: 'Zero or more', behavior: 'Related gallery records.' },
    { name: 'URLs', cardinality: 'Zero or more', behavior: 'External links associated with the record.' },
    { name: 'Remote IDs', cardinality: 'Zero or more', behavior: 'Provider endpoint and remote identifier pairs used to reconnect external metadata.' },
    { name: 'Custom Fields', cardinality: 'Zero or more', behavior: 'Administrator-defined video fields and values.' },
    { name: 'Parent video', cardinality: 'Zero or one', behavior: 'Present for a sub-video; clip boundaries define its playable range within the root parent.' },
  ],
  files: [
    { name: 'Path', meaning: 'Cove-visible filesystem path; containerized installations show the container path.' },
    { name: 'File Size', meaning: 'Size in bytes, formatted for display.' },
    { name: 'Format', meaning: 'Container format reported during scanning.' },
    { name: 'Duration', meaning: 'Playback duration.' },
    { name: 'Dimensions', meaning: 'Pixel width × height.' },
    { name: 'Frame Rate', meaning: 'Frames per second.' },
    { name: 'Bitrate', meaning: 'Kilobits per second in the interface.' },
    { name: 'Video Codec and Audio Codec', meaning: 'Codec names reported during scanning.' },
    { name: 'Fingerprints', meaning: 'Available hashes such as oshash, md5, or phash.' },
  ],
  capabilities: [
    { name: 'Playback', support: 'Built in', behavior: 'Uses an attached file or resolves the root parent file for a sub-video.' },
    { name: 'Sidecar captions', support: 'Built in', behavior: 'Discovered VTT and SRT tracks are associated with individual files and used by the player.' },
    { name: 'Raw segments and segments', support: 'Supported', behavior: 'Timeline points and ranges can be stored and resolved through display profiles.' },
    { name: 'Sub-videos', support: 'Supported', behavior: 'A child video can present a bounded range of a parent source.' },
    { name: 'Similarity', support: 'Optional', behavior: 'Visual and audio similarity surfaces require corresponding configured services.' },
    { name: 'Generated media', support: 'Conditional', behavior: 'Covers, thumbnails, previews, sprites, and other derivatives depend on generators and jobs.' },
  ],
  availability: [
    { name: 'Browsing and detail', requirement: 'Requires video-read access; content rules can further determine which records are visible.' },
    { name: 'Playback', requirement: 'Requires access to the video, streaming access, and an available resolved file.' },
    { name: 'Editing', requirement: 'Requires video-write access.' },
    { name: 'File Info', requirement: 'Requires file-read access.' },
    { name: 'Scans and jobs', requirement: 'Require the relevant operation permission and an available scan or job service.' },
    { name: 'Provider operations', requirement: 'Require suitable metadata access and a configured provider.' },
    { name: 'Extension surfaces', requirement: 'Appear only when the contributing extension is installed and enabled and the operation is authorized.' },
  ],
  lifecycle: [
    { operation: 'Create without a file', effect: 'The record can exist before a downloader supplies media or while its file is unavailable.' },
    { operation: 'Delete record', effect: 'Removes the video record without inherently deleting its source file.' },
    { operation: 'Delete source file', effect: 'Requires an explicit deletion choice and a writable library path.' },
    { operation: 'Bulk-delete generated files', effect: 'A separate choice can remove derivatives independently of source files.' },
    { operation: 'Delete parent video', effect: 'Also deletes its sub-video records.' },
    { operation: 'Delete sub-video', effect: 'Does not delete the parent source files.' },
    { operation: 'Delete a shared path', effect: 'Cove retains the source path when a video outside the deletion still references it.' },
  ],
});

export const audioReference = defineMediaReference({
  fields: [
    { name: 'Title', meaning: 'Display title for the audio record.' },
    { name: 'Date', meaning: 'Calendar date associated with the audio record.' },
    { name: 'Code', meaning: 'Studio- or publisher-assigned catalog code.' },
    { name: 'Details', meaning: 'Longer free-text description.' },
    { name: 'Organized', meaning: 'User-managed organization state.' },
    { name: 'Cover', meaning: 'Image used to represent the audio record.' },
    { name: 'Created and Updated', meaning: 'Timestamps maintained by Cove for the record.' },
  ],
  relationships: [
    { name: 'Studio', cardinality: 'Zero or one', behavior: 'Primary studio associated with the audio record.' },
    { name: 'Tags', cardinality: 'Zero or more', behavior: 'Reusable classification metadata.' },
    { name: 'Performers', cardinality: 'Zero or more', behavior: 'Associated performer identities.' },
    { name: 'Groups', cardinality: 'Zero or more', behavior: 'Group memberships that include the audio record.' },
    { name: 'URLs', cardinality: 'Zero or more', behavior: 'External links associated with the record.' },
    { name: 'Custom Fields', cardinality: 'Zero or more', behavior: 'Administrator-defined audio fields and values.' },
  ],
  files: [
    { name: 'Path', meaning: 'Cove-visible filesystem path.' },
    { name: 'File Size', meaning: 'Size in bytes, formatted for display.' },
    { name: 'Format', meaning: 'Media format reported during scanning.' },
    { name: 'Duration', meaning: 'Playback duration.' },
    { name: 'Audio Codec', meaning: 'Codec reported during scanning.' },
    { name: 'Bitrate', meaning: 'Audio bitrate.' },
    { name: 'Sample Rate', meaning: 'Samples per second when discovered.' },
    { name: 'Channels', meaning: 'Channel count when discovered.' },
    { name: 'Video Track', meaning: 'Whether the file also contains a video track.' },
    { name: 'Fingerprints', meaning: 'Available file hashes.' },
  ],
  capabilities: [
    { name: 'Playback', support: 'Built in', behavior: 'Plays an available attached audio file.' },
    { name: 'Tracks', support: 'Supported', behavior: 'Named, ordered start and end ranges can describe subdivisions of one audio record.' },
    { name: 'Raw segments', support: 'Supported', behavior: 'Audio can host stored points and time ranges.' },
    { name: 'Sub-videos', support: 'Not applicable', behavior: 'Audio ranges do not become video child records.' },
    { name: 'Generated media', support: 'Conditional', behavior: 'Cover and other derivative operations depend on configured generators and jobs.' },
  ],
  availability: [
    { name: 'Browsing and detail', requirement: 'Requires audio-read access; content rules can further determine which records are visible.' },
    { name: 'Editing', requirement: 'Requires audio-write access.' },
    { name: 'Playback', requirement: 'Requires access to the audio record, streaming access, and an available file.' },
    { name: 'File operations', requirement: 'Require the relevant file access; source changes also require a writable library path.' },
    { name: 'Provider operations', requirement: 'Require suitable metadata access and a configured provider.' },
    { name: 'Extension surfaces', requirement: 'Appear only when the contributing extension is installed and enabled and the operation is authorized.' },
  ],
  lifecycle: [
    { operation: 'Create without a file', effect: 'The record can exist before a downloader supplies media or while its file is unavailable.' },
    { operation: 'Delete record', effect: 'Removes the audio record without inherently deleting its source file.' },
    { operation: 'Delete source file', effect: 'Requires an explicit deletion choice and a writable library path.' },
    { operation: 'Delete generated media', effect: 'Derivative assets have a lifecycle separate from the source file.' },
    { operation: 'Delete a shared path', effect: 'Cove retains the source path when an audio record outside the deletion still references it.' },
  ],
});

export const imageReference = defineMediaReference({
  fields: [
    { name: 'Title', meaning: 'Display title for the image.' },
    { name: 'Date', meaning: 'Calendar date associated with the image.' },
    { name: 'Code', meaning: 'Studio- or publisher-assigned catalog code.' },
    { name: 'Photographer', meaning: 'Free-text photographer value.' },
    { name: 'Details', meaning: 'Longer free-text description.' },
    { name: 'Organized', meaning: 'User-managed organization state.' },
    { name: 'Created and Updated', meaning: 'Timestamps maintained by Cove for the record.' },
  ],
  relationships: [
    { name: 'Studio', cardinality: 'Zero or one', behavior: 'Primary studio associated with the image.' },
    { name: 'Tags', cardinality: 'Zero or more', behavior: 'Reusable classification metadata.' },
    { name: 'Performers', cardinality: 'Zero or more', behavior: 'Associated performer identities.' },
    { name: 'Galleries', cardinality: 'Zero or more', behavior: 'Collection context without changing the image identity.' },
    { name: 'Groups', cardinality: 'Zero or more', behavior: 'Group memberships that include the image.' },
    { name: 'URLs', cardinality: 'Zero or more', behavior: 'External links associated with the record.' },
    { name: 'Custom Fields', cardinality: 'Zero or more', behavior: 'Administrator-defined image fields and values.' },
  ],
  files: [
    { name: 'Path', meaning: 'Cove-visible filesystem path.' },
    { name: 'File Size', meaning: 'Size in bytes, formatted for display.' },
    { name: 'Format', meaning: 'Image format reported during scanning.' },
    { name: 'Dimensions', meaning: 'Pixel width × height.' },
    { name: 'Fingerprints', meaning: 'Available file hashes, including perceptual hashes when generated.' },
  ],
  capabilities: [
    { name: 'Library list', support: 'Built in', behavior: 'Supports search, filters, sorts, selection actions, and extension-contributed actions.' },
    { name: 'Gallery membership', support: 'Supported', behavior: 'One image can belong to more than one gallery.' },
    { name: 'Raw segments', support: 'Supported', behavior: 'Images can host point-like annotations or analysis records.' },
    { name: 'Detections', support: 'Conditional', behavior: 'Configured analysis services can attach detected regions and related data.' },
    { name: 'Similarity', support: 'Optional', behavior: 'Similarity surfaces require a corresponding configured service.' },
    { name: 'Playback', support: 'Not applicable', behavior: 'An image has no playback timeline.' },
    { name: 'Generated media', support: 'Conditional', behavior: 'Thumbnails, previews, and analysis output depend on configured generators and jobs.' },
  ],
  availability: [
    { name: 'Browsing and detail', requirement: 'Requires image-read access; content rules can further determine which records are visible.' },
    { name: 'Editing', requirement: 'Requires image-write access.' },
    { name: 'File operations', requirement: 'Require the relevant file access; source changes also require a writable library path.' },
    { name: 'Similarity', requirement: 'Requires a corresponding configured service, available results, and authorized access.' },
    { name: 'Faces', requirement: 'Requires stored face detections and face-read access; the producing analysis service does not need to remain available.' },
    { name: 'Provider operations', requirement: 'Require suitable metadata access and a configured provider.' },
    { name: 'Extension surfaces', requirement: 'Appear only when the contributing extension is installed and enabled and the operation is authorized.' },
  ],
  lifecycle: [
    { operation: 'Create without a file', effect: 'The record can remain present while its file is temporarily unavailable.' },
    { operation: 'Remove gallery membership', effect: 'Does not delete the image record or source file.' },
    { operation: 'Delete record', effect: 'Removes the image record without inherently deleting its source file.' },
    { operation: 'Delete source file', effect: 'Requires an explicit deletion choice and a writable library path.' },
    { operation: 'Delete generated media', effect: 'Derivative assets have a lifecycle separate from the source file.' },
    { operation: 'Delete a shared path', effect: 'Cove retains the source path when an image record outside the deletion still references it.' },
  ],
});
