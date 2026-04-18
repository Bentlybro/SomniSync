import React from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';

export default function Home(): React.JSX.Element {
  return (
    <Layout title="SomniSync" description="High-performance real-time physics networking for Unity">
      <main style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', minHeight: '70vh', padding: '2rem' }}>
        <h1 style={{ fontSize: '3rem', marginBottom: '0.5rem' }}>SomniSync</h1>
        <p style={{ fontSize: '1.3rem', color: 'var(--ifm-color-emphasis-600)', marginBottom: '2rem', textAlign: 'center', maxWidth: '600px' }}>
          Server-authoritative real-time physics networking for Unity.
          <br />
          Rust + Rapier3D backend, binary wire protocol, per-client AOI.
        </p>
        <div style={{ display: 'flex', gap: '1rem' }}>
          <Link className="button button--primary button--lg" to="/docs">
            Get Started
          </Link>
          <Link className="button button--secondary button--lg" href="https://github.com/Bentlybro/SomniSync">
            GitHub
          </Link>
        </div>
      </main>
    </Layout>
  );
}
