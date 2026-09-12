const vm = require('node:vm');
const fs = require('node:fs');
const assert = require('node:assert/strict');
(async () => {
 for (const [family, name] of [['chromium', 'chrome'], ['firefox', 'browser']]) {
  for (const status of ['queued', 'declined', 'error', 'disconnect']) {
   const events = []; let onCreated; let message; let disconnect;
   const event = () => ({ addListener() {} });
   const api = {
    runtime: { id: 'test-extension', getManifest(){return {version:'test'};}, onInstalled: event(), connectNative() { return {
     onMessage: {addListener(fn) {message=fn;}}, onDisconnect: {addListener(fn) {disconnect=fn;}},
     disconnect() {}, postMessage(payload) { events.push(['send',payload]); queueMicrotask(() => status === 'disconnect' ? disconnect() : message({status})); }
    }; } },
    contextMenus: {onClicked:event()},
    storage: {onChanged:event(),local:{get(defaults,cb){cb?.(defaults);return Promise.resolve(defaults);}}},
    cookies: {async getAll(){return [{name:'session',value:'test'}];}},
    action: {setTitle(){},setBadgeText(){},setBadgeBackgroundColor(){},setIcon(){}},
    downloads: {onCreated:{addListener(fn){onCreated=fn;}},async pause(){events.push(['pause']);},async resume(){events.push(['resume']);},async cancel(){events.push(['cancel']);},async removeFile(){events.push(['removeFile']);},async erase(){events.push(['erase']);}}
   };
   vm.runInNewContext(fs.readFileSync(`extensions/${family}/background.js`,'utf8'), {[name]:api,navigator:{userAgent:'test-agent'},setTimeout(){},console,queueMicrotask});
   await onCreated({id:1,url:'https://example.test/file',filename:'C:\\Downloads\\test.zip',totalBytes:123,referrer:'https://example.test/'});
   assert.deepEqual(events.map(e=>e[0]), status==='queued' ? ['pause','send','pause','cancel','removeFile','erase'] : ['pause','send','resume']);
   assert.equal(events[1][1].totalBytes,123); assert.equal(events[1][1].cookie,'session=test');
   assert.equal(events[1][1].suggestedFileName,'test.zip');
   events.length=0; await onCreated({id:2,url:'blob:test',totalBytes:-1}); assert.equal(events.length,0);
   console.log(`PASS ${family} ${status}`);
  }
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
