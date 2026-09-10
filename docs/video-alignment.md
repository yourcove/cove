# Primary video files and timeline alignment

Every video with attached media has one explicit primary file. Playback, generated previews, and timeline-based content use that file. The File Info tab identifies it and offers actions on the other attached files: play the file temporarily, set it as primary, move it to another video, split it into a separate video, or delete it.

Playing a non-primary file is temporary. The player shows an alternate-file banner, hides segments and detections, and does not record playback progress because those values use the primary timeline. **Return to primary** restores normal playback.

When **Set as primary** is selected, Cove first compares the files' pHashes and durations. Matching pHashes within tolerance and a duration difference of at most one second allow a direct switch. If there are no timed dependencies, the switch is also direct.

If the files differ and the video has segments, clips, detections, or group ranges, the dialog offers alignment or removal of the timed content. Alignment samples short windows across both videos and proposes visual anchors. After analysis, the dialog shows color frames from the current and proposed primary side by side at mapped positions for up to five segments and five clips. A dependency must fit within a confirmed matching section or be explicitly selected for removal before the primary changes.

Samples, thumbnails, anchors, and alignment maps exist only while the dialog is open. Applying an alignment rewrites the affected timestamps and discards the map. Closing or cancelling the dialog stores nothing. If the previous primary is already unavailable, Cove cannot infer an alignment; timed content must be deleted before another primary can be assigned. Deleting an unresolved clip deletes that clip video and its nested content, so the dialog requires a separate confirmation before applying that choice.

Changing the primary and rewriting or removing dependencies is one database save. Cove rejects a stale dialog if the primary file changed in the meantime. Generated video assets are invalidated after a successful switch, and HLS output is keyed by the selected file so an earlier primary cannot supply stale segments.
