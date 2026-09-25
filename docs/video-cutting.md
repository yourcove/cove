# Cutting parts out of videos

Removing footage you don't want is often the quickest way to reclaim space, especially from long or very large files such as VR videos. Cove can cut time ranges out of a video's primary file, and move everything timed on the video with the cut.

## In the app

On a video's page, choose **Trim…** from the actions menu. A timeline opens under the player:

- Drag across the timeline to mark a part to remove. Drag a marked part's edge to move it, and click the timeline to seek.
- **Remove start → here** and **Remove here → end** mark from the playhead to either end of the video. **Mark in** followed by **Remove … → …** marks between two playhead positions.
- Marked times can also be typed as `1:02:03.5`, `2:03` or plain seconds.
- **Skip removed parts while playing** jumps over marked parts, so you can watch the result before cutting.

While you mark, Cove previews the cut. The preview shows how much is kept, roughly how much space it saves, and what happens to each marker, clip, detection and group range on the video.

There are two ways to cut:

- **Fast (no re-encode)** copies the kept footage without decoding it. It takes seconds and loses no quality, but a kept part can only start on a keyframe. Each part therefore starts on the keyframe at or just before its mark, so slightly more is kept than marked. The timeline shows that extra footage in amber.
- **Exact (re-encode)** cuts exactly on the marks and re-encodes the kept footage at the chosen quality, using the same quality search as video conversion. It can also lower the frame rate, as a conversion can. Because the rest of the file is re-encoded as well, this also shrinks it. When one continuous part is kept, the audio is copied. When several parts are joined, the audio is re-encoded to AAC so it stays in step at the joins, and subtitle tracks are dropped because they cannot be joined.

Chapters are dropped from every cut, since they describe the old timeline.

Without **Replace the original**, the cut file is added to the video next to the original, and nothing else changes. With it, Cove:

1. Checks that the cut file decodes cleanly and has the expected length.
2. Makes the cut file the video's primary file.
3. Moves everything timed on the video onto the new timeline, in the same database transaction.
4. Deletes the original from disk.
5. Regenerates the cover, previews and sprite that the video had.

Timed items move as follows:

- An item after a removed part moves earlier by the length removed before it.
- An item that lies entirely inside removed footage is deleted.
- An item that spans a cut is kept, and now covers its kept parts back to back.
- A point in time, such as a detection, is kept only if it lies in kept footage.

## For extensions

Proposing cuts only needs the HTTP API and a link. An extension decides which parts to remove, and the user reviews and applies them.

### Open the editor pre-filled

Link to a video with a `cut` parameter to open its trim editor with those removals already marked:

```
/video/123?cut=0-95.5,1800-1932
```

Each removal is written as `start-end` in seconds, and removals are separated by commas. From extension UI code, navigate to `{ page: "video", id: 123, cut: [{ start: 0, end: 95.5 }, { start: 1800, end: 1932 }] }`. The user sees the removals, their effect on markers and clips, and the space saved, and chooses whether to cut. A malformed parameter is ignored as a whole, so the editor never opens with a guess.

### Preview a cut

`POST /api/videos/{videoId}/cut/preview` takes `{ "remove": [{ "start": 0, "end": 95.5 }], "exact": false }` and changes nothing. The response contains:

- **`fileId`**: the primary file the preview read. Pass it back when cutting.
- **`kept`**: where the kept parts really start and end. For a lossless cut, these are after the move back to keyframes.
- **`outputDuration`** and **`removedSeconds`**.
- **`sourceBytes`** and **`estimatedBytes`**: the estimate assumes kept footage costs what it did in the original.
- **`items`**: every timed item on the video, with its `outcome` (`kept`, `moved`, `joined` or `removed`) and its new times.

It needs `videos.read` and `files.read`. Markers are included only for callers with `segments.read`.

### Cut

`POST /api/videos/cut` queues one background job for any number of videos:

```json
{
  "videos": [{ "videoId": 123, "fileId": 456, "remove": [{ "start": 0, "end": 95.5 }] }],
  "codec": "copy",
  "container": "source",
  "effort": "highHardware",
  "replaceOriginal": true
}
```

- **`codec`**: `copy` (the default) cuts losslessly. `h264`, `hevc` or `av1` cut exactly while re-encoding, at the quality named by `effort`, which takes the same values as conversion.
- **`outputFrameRate`**: re-encode at this lower frame rate, such as `30`. It needs a codec other than `copy`.
- **`container`**: `source` (the default) keeps each file's format. `mp4` or `mkv` can be chosen instead.
- **Stale times**: a video whose primary file is no longer `fileId` is refused, because the times would no longer describe the same footage.
- **Permissions**: the request needs `jobs.run` and `videos.write`. `replaceOriginal` also needs `videos.delete.file`, plus permission to change or delete the markers, clips and group ranges the cut affects. That is checked when the request is made and again when the job reaches the video.
- **Response**: the job's id. Follow the job as you would any other.
