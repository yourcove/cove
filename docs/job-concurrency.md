# Background job limits

Settings > Operations > Jobs has two independent controls:

- **Max CPU threads for a single job** (formerly Max parallel tasks) controls parallel work items inside supported jobs, such as scan, generation, and bulk deletion. The saved setting remains maxParallelTasks, so existing values are preserved. -1 uses available CPU threads. This is not a strict operating-system thread cap; tools and extensions can create their own threads.
- **Max simultaneous jobs** controls how many background jobs can run at once, including extension jobs. It defaults to 1, accepts 1 through 128, and is saved as maxSimultaneousJobs.

For example, set simultaneous jobs to 2 to allow tagging and bulk deletion to run together. Set per-job parallelism to 2 to allow supported jobs to process two items at a time within each job.

Jobs wait in the reorderable pending queue when all slots are occupied. Completion, failure, and cancellation release a slot only after the callback has finished unwinding. A saved increase starts waiting jobs without a restart. A decrease leaves current jobs running and starts further work only after the running count falls below the new limit.

The legacy SDK exclusive argument remains available for extension binary compatibility. Both values now enter the same bounded queue; exclusive: false no longer bypasses job limits.
