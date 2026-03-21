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
      ],
    },
    {
      type: 'category',
      label: 'Components',
      items: [
        'components/network-manager',
        'components/network-object',
        'components/network-variables',
        'components/network-events',
        'components/network-animator',
        'components/network-rigidbody',
        'components/network-particle',
        'components/network-timer',
        'components/network-stats',
        'components/player-push-detector',
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
