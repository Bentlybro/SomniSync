import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'SomniSync',
  tagline: 'Shared worlds. Less wire traffic.',
  favicon: 'img/somnisync-mark.svg',
  url: 'https://bentlybro.github.io',
  baseUrl: '/SomniSync/',
  organizationName: 'Bentlybro',
  projectName: 'SomniSync',
  trailingSlash: false,
  onBrokenLinks: 'throw',
  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/Bentlybro/SomniSync/tree/master/website/',
          routeBasePath: 'docs',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    navbar: {
      title: 'SomniSync',
      logo: {
        alt: 'SomniSync',
        src: 'img/somnisync-mark.svg',
      },
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docsSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          to: '/docs/getting-started/setup',
          label: 'Get Started',
          position: 'left',
        },
        {
          to: '/docs/ops/benchmarking',
          label: 'Benchmarks',
          position: 'left',
        },
        {
          href: 'https://github.com/Bentlybro/SomniSync',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            { label: 'Getting Started', to: '/docs/getting-started/setup' },
            { label: 'Components', to: '/docs/components/network-manager' },
            { label: 'Examples', to: '/docs/examples/basic-world' },
          ],
        },
        {
          title: 'Community',
          items: [
            { label: 'GitHub', href: 'https://github.com/Bentlybro/SomniSync' },
          ],
        },
      ],
      copyright: `SomniSync · Mozilla Public License 2.0`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'rust', 'bash'],
    },
    colorMode: {
      defaultMode: 'dark',
      respectPrefersColorScheme: true,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
