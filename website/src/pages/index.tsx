import React from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';

export default function Home(): React.JSX.Element {
  return (
    <Layout
      title="Networking for Somnium worlds"
      description="SomniSync connects Unity worlds to a Rust physics relay. Setup instructions, component reference, deployment, and measured performance."
    >
      <main className="home">
        <section className="home-intro">
          <div className="home-wrap home-intro-grid">
            <div>
              <p className="home-label">SomniSync / Open source networking</p>
              <h1>Networking for<br />Somnium worlds.</h1>
              <p className="home-lead">
                A Unity package and a Rust relay for shared physics in Somnium Space.
                The relay simulates objects and sends compact updates to players in the same room.
              </p>
              <div className="home-links">
                <Link className="home-primary-link" to="/docs/getting-started/setup">Read the setup guide</Link>
                <Link to="/docs">Browse the documentation</Link>
                <Link href="https://github.com/Bentlybro/SomniSync">Source on GitHub</Link>
              </div>
            </div>
            <aside className="home-facts" aria-label="Project facts">
              <h2>At a glance</h2>
              <dl>
                <div><dt>Client</dt><dd>Unity 6, including Somnium world scripts</dd></div>
                <div><dt>Server</dt><dd>Rust with Rapier3D physics</dd></div>
                <div><dt>Transport</dt><dd>WebSocket with a compact binary protocol</dd></div>
                <div><dt>License</dt><dd>Mozilla Public License 2.0</dd></div>
                <div><dt>Status</dt><dd>Public preview</dd></div>
              </dl>
            </aside>
          </div>
        </section>

        <section className="home-section">
          <div className="home-wrap">
            <div className="home-section-heading">
              <p className="home-label">Start here</p>
              <h2>Two parts, one shared scene.</h2>
            </div>
            <div className="home-two-column">
              <article>
                <p className="home-index">01 / Server</p>
                <h3>Run the relay</h3>
                <p>The relay owns the physics simulation and broadcasts object state. Docker Compose starts it with the game socket and metrics dashboard bound to loopback by default.</p>
                <pre><code>git clone https://github.com/Bentlybro/SomniSync.git{ '\n' }cd SomniSync{ '\n' }docker compose up -d --build</code></pre>
                <p>For a public world, add a TLS reverse proxy and give Unity its <code>wss://</code> address.</p>
                <Link to="/docs/getting-started/server-deployment">Server deployment guide</Link>
              </article>
              <article>
                <p className="home-index">02 / Unity</p>
                <h3>Prepare the world</h3>
                <p>Add the Unity package from Git, then open the SomniSync setup wizard. For a Somnium upload, the wizard copies scripts into your creator folder with the namespace Somnium expects.</p>
                <pre><code>https://github.com/Bentlybro/SomniSync.git?path=client/Unity</code></pre>
                <p>Add a manager and network objects, validate the scene, and test with two clients.</p>
                <Link to="/docs/getting-started/setup">Unity setup guide</Link>
              </article>
            </div>
          </div>
        </section>

        <section className="home-section home-tinted">
          <div className="home-wrap home-explain">
            <div>
              <p className="home-label">What it does</p>
              <h2>Physics runs on the relay.</h2>
            </div>
            <div>
              <p>Clients send movement, interactions, and game events. The relay steps the shared scene and sends state back. Sleeping objects send no position updates. When a client's position is known, distance filtering reduces updates for objects farther away.</p>
              <p>Components cover physics objects, variables, events, animation, particles, timers, and NPCs. You can add the parts your world needs.</p>
              <Link to="/docs/getting-started/which-components">Choose components</Link>
            </div>
          </div>
        </section>

        <section className="home-section">
          <div className="home-wrap">
            <div className="home-section-heading">
              <p className="home-label">Measured performance</p>
              <h2>Load test results</h2>
              <p>Tests ran on an isolated two-vCPU VPS using simple generated spheres. These numbers describe those runs, not a guaranteed limit for every world.</p>
            </div>
            <div className="home-table-wrap">
              <table className="home-table">
                <thead><tr><th scope="col">Moving objects</th><th scope="col">Clients</th><th scope="col">Received per client</th><th scope="col">Physics step p95</th></tr></thead>
                <tbody>
                  <tr><td>4,000</td><td>4</td><td>19.5 frames/s, 283 KB/s</td><td>13.4 ms</td></tr>
                  <tr><td>4,000</td><td>40 synthetic clients</td><td>19.7 frames/s, 288 KB/s</td><td>13.0 ms</td></tr>
                  <tr><td>8,000</td><td>2</td><td>About 20 frames/s, 480 KB/s</td><td>28.7 ms at adaptive 30 Hz</td></tr>
                </tbody>
              </table>
            </div>
            <p className="home-caption">No dropped sends were reported in these runs. Complex colliders, rendering, many rooms, and longer sessions need their own tests. <Link to="/docs/ops/benchmarking">Benchmark your scene</Link>.</p>
          </div>
        </section>

        <section className="home-section home-status">
          <div className="home-wrap home-explain">
            <div>
              <p className="home-label">Release status</p>
              <h2>A working preview, with clear limits.</h2>
            </div>
            <div>
              <p>The relay does not authenticate players or enforce game-specific rules. Room names are public routing keys. Persistence is disabled by default because client-supplied IDs do not prove identity.</p>
              <p>Keep the metrics dashboard private, expose the game socket through TLS, and validate your world under realistic load before inviting players.</p>
              <Link to="/docs/getting-started/server-deployment">Read the deployment and security notes</Link>
            </div>
          </div>
        </section>
      </main>
    </Layout>
  );
}
