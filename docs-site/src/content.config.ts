import { defineCollection } from 'astro:content';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';
import { z } from 'astro/zod';

const compatibilitySchema = z.object({
  api: z.boolean().optional(),
  sdk: z.boolean().optional(),
  sources: z.array(z.string().regex(/^(?:src|ui\/src)\//)).min(1),
});

export const collections = {
  docs: defineCollection({
    loader: docsLoader(),
    schema: docsSchema({
      extend: z.object({
        compatibility: compatibilitySchema.optional(),
      }),
    }),
  }),
};
