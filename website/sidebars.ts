import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docsSidebar: [
    'intro',
    {
      type: 'category',
      label: 'Getting Started',
      items: [
        'getting-started/setup',
        'getting-started/which-components',
        'getting-started/extending-components',
      ],
    },
    {
      type: 'category',
      label: 'Components',
      items: [
        'components/network-manager',
        'components/network-object',
        'components/network-npc',
        'components/network-variables',
        'components/network-events',
        'components/network-animator',
        'components/network-rigidbody',
        'components/network-particle',
        'components/network-timer',
        'components/network-persistence',
        'components/network-stats',
        'components/player-push-detector',
      ],
    },
    {
      type: 'category',
      label: 'Operations',
      items: [
        'ops/dashboard',
        'ops/benchmarking',
      ],
    },
    {
      type: 'category',
      label: 'Examples',
      items: [
        'examples/basic-world',
        'examples/interactive-door',
        'examples/physics-playground',
        'examples/dungeon-room',
        'examples/vehicle',
        'examples/scoreboard',
      ],
    },
    {
      type: 'category',
      label: 'Protocol',
      items: [
        'protocol/overview',
      ],
    },
  ],
};

export default sidebars;
