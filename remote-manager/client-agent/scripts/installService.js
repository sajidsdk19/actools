/**
 * Install ac-client-agent as a Windows Service using node-windows.
 * Run once as Administrator: node scripts/installService.js
 */
const Service = require('node-windows').Service;
const path    = require('path');

const svc = new Service({
  name:        'AcRemoteAgent',
  description: 'Assetto Corsa Remote Session Agent',
  script:      path.join(__dirname, '..', 'src', 'index.js'),
  nodeOptions: [],
  env: [
    { name: 'NODE_ENV', value: 'production' },
  ],
});

svc.on('install',   () => { svc.start(); console.log('Service installed and started.'); });
svc.on('alreadyinstalled', () => console.log('Already installed.'));
svc.on('error',     (e) => console.error('Service error:', e));

svc.install();
