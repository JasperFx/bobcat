<script setup lang="ts">
import type { StepAttachment } from '@/stores/runs-store'

/**
 * Failure evidence (bobcat#571): an image inline, where a screenshot is read; anything else as a
 * link to the file; an attachment whose bytes were not kept as its name and the reason.
 */
defineProps<{ attachments: StepAttachment[]; suppressed?: number }>()

function sizeOf(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}
</script>

<template>
  <ul class="bm-attachments" data-testid="attachments">
    <li v-for="(attachment, i) in attachments" :key="i" class="bm-attachment" data-testid="attachment">
      <template v-if="attachment.href">
        <a :href="attachment.href" target="_blank" rel="noopener">{{ attachment.name }}</a>
        <span class="bm-attachment-meta">{{ attachment.mediaType }}, {{ sizeOf(attachment.size) }}</span>
        <img
          v-if="attachment.mediaType.startsWith('image/')"
          :src="attachment.href"
          :alt="attachment.name"
          class="bm-attachment-image"
          loading="lazy"
        />
      </template>
      <template v-else>
        <span>{{ attachment.name }}</span>
        <span class="bm-attachment-meta" data-testid="attachment-not-kept">
          {{ attachment.omitted ?? 'not kept with these results' }}
        </span>
      </template>
    </li>
    <li v-if="suppressed" class="bm-attachment-meta">…and {{ suppressed }} more</li>
  </ul>
</template>

<style scoped>
.bm-attachments {
  list-style: none;
  margin: 2px 0;
  padding-left: 1.5em;
  font-size: 12px;
}

.bm-attachment a {
  color: var(--bm-link);
}

.bm-attachment-meta {
  margin-left: 6px;
  opacity: 0.7;
}

.bm-attachment-image {
  display: block;
  max-width: min(100%, 960px);
  margin: 4px 0;
  border: 1px solid var(--bm-divider);
}
</style>
