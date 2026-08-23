import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://dev.standardbeagle.com',
  base: '/ps-agent',
  integrations: [
    starlight({
      title: 'ps-agent',
      description: 'Two PowerShell cmdlets that put an agent in your terminal: a minimal coding agent and an Agent Client Protocol client, sharing one transcript model.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/standardbeagle/ps-agent' },
      ],
      head: [
        {
          tag: 'meta',
          attrs: { property: 'og:title', content: 'ps-agent' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:description', content: 'Two PowerShell cmdlets that put an agent in your terminal: a minimal coding agent and an Agent Client Protocol client, sharing one transcript model.' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:type', content: 'website' },
        },
        {
          tag: 'meta',
          attrs: { property: 'og:url', content: 'https://dev.standardbeagle.com/ps-agent/' },
        },
        {
          tag: 'meta',
          attrs: { name: 'twitter:card', content: 'summary' },
        },
      ],
      customCss: ['./src/styles/custom.css'],
      sidebar: [
        {
          label: 'Start Here',
          items: [
            { label: 'Getting Started', slug: 'getting-started' },
            { label: 'The Transcript', slug: 'transcript' },
          ],
        },
        {
          label: 'Commands',
          items: [
            { label: 'Invoke-Agent', slug: 'commands/invoke-agent' },
            { label: 'Invoke-Acp', slug: 'commands/invoke-acp' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Authentication', slug: 'reference/authentication' },
            { label: 'Tools & Safety', slug: 'reference/tools-and-safety' },
            { label: 'ACP', slug: 'reference/acp' },
          ],
        },
      ],
    }),
  ],
});
