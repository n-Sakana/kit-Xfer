"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const core = require("../web/optical-core.js");
function frame(seq = 0, session = 123, length = 980, total = 1800) {
  const bytes = new Uint8Array(length + 20), v = new DataView(bytes.buffer);
  bytes[0] = 0xd1; bytes[1] = 0x0c;
  v.setUint16(2, session, true); v.setUint32(4, seq, true);
  v.setUint16(8, Math.ceil(total / length), true); v.setUint16(10, length, true);
  v.setUint32(12, total, true); v.setUint32(16, 0xdeadbeef, true);
  return bytes;
}
function packed(side = 29, seq = 0, count = 2) {
  const data = new ArrayBuffer(12 + Math.ceil(side * side / 8) * count);
  const v = new DataView(data); v.setUint32(0, 0x46515231, false);
  v.setUint16(4, side, true); v.setUint16(6, count, true); v.setUint32(8, seq, true);
  new Uint8Array(data)[12] = 0x81;
  return data;
}
function queue(overrides = {}) {
  return new core.UploadQueue(Object.assign({send: async () => ({recognized:true}),
    onProgress: () => {}, onFatal: error => { throw error; }}, overrides));
}

test("valid binary frame and byte-offset view", () => {
  const b = frame(0xffffffff), padded = new Uint8Array(b.length + 9);
  padded.set(b, 7);
  const parsed = core.parseFrame(padded.subarray(7, 7 + b.length));
  assert.equal(parsed.sequence, 0xffffffff); assert.equal(parsed.bytes.length, 1000);
  assert.equal(core.parseFrame(b.buffer).session, parsed.session);
});
test("all truncated header lengths rejected", () => {
  for (let n = 0; n <= 20; n++) assert.equal(core.parseFrame(frame().slice(0, n)), null);
});
test("foreign QR, invalid input type and malformed stream rejected", () => {
  for (const value of [null, undefined, {}, -1, "999999999", new TextEncoder().encode("https://example.test/")])
    assert.equal(core.parseFrame(value), null);
  for (const offset of [0, 1, 8, 10, 12]) {
    const b = frame(); b[offset] = 0; if (offset >= 8) b[offset + 1] = 0;
    assert.equal(core.parseFrame(b), null);
  }
  assert.equal(core.parseFrame(frame().slice(0, -1)), null);
});
test("max QR length and max container size boundaries", () => {
  assert.ok(core.parseFrame(frame(0, 1, 2933, 65 * 1024 * 1024 + 64 * 1024)));
  assert.equal(core.parseFrame(frame(0, 1, 2934)), null);
  assert.equal(core.parseFrame(frame(0, 1, 2933, 65 * 1024 * 1024 + 64 * 1024 + 1)), null);
});
test("packed bitmap bit order, alpha and 64-bit request sequence", () => {
  const frames = core.unpackFrames(packed(29, 0xffffffff), 29, 0xffffffff, 2);
  assert.equal(frames.length, 2); assert.equal(frames[1].sequence, 4294967296);
  assert.deepEqual(Array.from(frames[0].pixels.slice(0, 8)), [0,0,0,255,255,255,255,255]);
  assert.equal(frames[0].pixels[7 * 4], 0);
  assert.ok(frames[1].pixels.every(v => v === 255));
});
test("packed format validates length/header/side/sequence/count", () => {
  for (const data of [new ArrayBuffer(0), packed().slice(0, -1), new ArrayBuffer(12)])
    assert.throws(() => core.unpackFrames(data, 29, 0, 2));
  assert.throws(() => core.unpackFrames(packed(), 33, 0, 2));
  assert.throws(() => core.unpackFrames(packed(), 29, 1, 2));
  assert.throws(() => core.unpackFrames(packed(), 29, 0, 1));
  assert.throws(() => core.unpackFrames(packed(189), 189, 0, 2));
});
test("version-40 packed batch reduces pixel bytes about 32-fold", () => {
  const b = packed(185, 0, 8);
  assert.equal(b.byteLength, 34244);
  assert.ok((185 * 185 * 4 * 8) / b.byteLength > 31.9);
  assert.equal(core.unpackFrames(b,185,0,8).length, 8);
});
test("no tracking, periodic full search, stale tracking, resolution change, loss reacquire", () => {
  const tracking = {left:400,right:800,top:200,bottom:600,width:1280,height:960,at:1000,misses:0};
  assert.equal(core.scanRegion(1280,960,null,1,1001).full, true);
  assert.equal(core.scanRegion(1280,960,tracking,1,1001).full, false);
  assert.equal(core.scanRegion(1280,960,tracking,6,1001).full, true);
  assert.equal(core.scanRegion(1280,960,tracking,1,2300).full, true);
  assert.equal(core.scanRegion(1920,1080,tracking,1,1001).full, true);
  assert.equal(core.scanRegion(1280,960,{...tracking,misses:3},1,1001).full, true);
});
test("tracking follows all edges without clipping outside camera", () => {
  for (const [x,y] of [[0,0],[1080,0],[0,760],[1080,760],[100,400]]) {
    const t={left:x,right:x+200,top:y,bottom:y+200,width:1280,height:960,at:0,misses:0};
    const r=core.scanRegion(1280,960,t,1,1);
    assert.ok(r.x>=0 && r.y>=0 && r.x+r.width<=1280 && r.y+r.height<=960);
    assert.ok(r.x<=x && r.x+r.width>=x+200 && r.y<=y && r.y+r.height>=y+200);
  }
});
test("ROI results map back to original video coordinates", () => {
  const t=core.trackPosition({topLeft:{x:10,y:20},topRight:{x:90,y:30},bottomRight:{x:100,y:100},bottomLeft:{x:15,y:95}},
    {x:300,y:400},1280,960,123);
  assert.equal(t.left,310); assert.equal(t.top,420); assert.equal(t.right,400); assert.equal(t.bottom,500);
  assert.equal(core.trackPosition({}, {x:0,y:0},1280,960,0),null);
});
test("EC capacities", () => {
  assert.deepEqual(["L","M","Q","H"].map(core.maximumFrameBytes),[2953,2331,1663,1273]);
});
test("deduplicate and pin first session", async () => {
  let posted=[]; const q=queue({send:async b => {posted.push(b);return {recognized:true};}});
  assert.equal(q.push(frame()),true); assert.equal(q.push(frame()),false);
  assert.equal(q.push(frame(1,124)),false); assert.equal(q.push(new Uint8Array(30)),false);
  await q.flush(); assert.equal(posted[0].length,1); assert.equal(q.push(frame()),false); q.stop();
});
test("batch size at most 8 and ordered requests", async () => {
  const seen=[]; const q=queue({send:async b => {seen.push(b.map(v=>core.parseFrame(v).sequence));return {};}});
  for(let i=0;i<20;i++) q.push(frame(i));
  await q.flush(); await q.flush(); await q.flush();
  assert.deepEqual(seen.map(b=>b.length),[8,8,4]); assert.deepEqual(seen.flat(),Array.from({length:20},(_,i)=>i)); q.stop();
});
test("bounded queue under a stalled connection", async () => {
  const q=queue(); for(let i=0;i<500;i++)q.push(frame(i));
  assert.equal(q.queue.length,128); assert.equal(q.keys.size,128); q.stop();
});
test("transient network/503 failure preserves same batch and retries", async () => {
  let calls=0, warnings=0, results=0, keys=[];
  const q=queue({send:async b=>{keys.push(core.parseFrame(b[0]).key);if(calls++===0)throw Object.assign(new Error("offline"),{status:503});return{};},
    onTransient:()=>warnings++,onProgress:()=>results++});
  q.push(frame(44)); await q.flush(); assert.equal(q.queue.length,1); assert.equal(warnings,1);
  await q.flush(); assert.equal(q.queue.length,0); assert.equal(results,1); assert.equal(keys[0],keys[1]); q.stop();
});
test("408 and 429 are retryable, 400/403/404 are fatal", async () => {
  for(const status of [408,429,400,403,404]) {
    let fatal=0;const q=queue({send:async()=>{throw Object.assign(new Error("HTTP"),{status});},onFatal:()=>fatal++});
    q.push(frame());await q.flush();assert.equal(fatal,status===408||status===429?0:1);q.stop();
  }
});
test("only one POST in flight", async () => {
  let resolve, calls=0; const q=queue({send:()=>{calls++;return new Promise(r=>resolve=r);}});
  q.push(frame()); const first=q.flush(); await q.flush(); assert.equal(calls,1);
  resolve({});await first;q.stop();
});
test("stop aborts request and late reply cannot update new session", async () => {
  let resolve, signal, updates=0;
  const q=queue({send:(_,s)=>{signal=s;return new Promise(r=>resolve=r);},onProgress:()=>updates++});
  q.push(frame());const pending=q.flush();q.stop();assert.equal(signal.aborted,true);
  resolve({});await pending;assert.equal(updates,0);assert.equal(q.queue.length,0);
  const next=queue();assert.equal(next.push(frame(0,999)),true);next.stop();
});
test("completion callback may stop queue safely", async () => {
  const q=queue({send:async()=>({complete:true}),onProgress:()=>q.stop()});
  q.push(frame());await q.flush();assert.equal(q.stopped,true);assert.equal(q.timer,null);
});
test("dedup history stays bounded after long transfer",async()=>{
  const q=queue();
  for(let i=0;i<2304;i+=8){for(let j=0;j<8;j++)q.push(frame(i+j));await q.flush();}
  assert.equal(q.history.length,2048);assert.equal(q.keys.size,2048);q.stop();
});

test("streaming frame keeps 64-bit chunk identity and rejects malformed lengths", () => {
  const bytes = new Uint8Array(245), view = new DataView(bytes.buffer);
  bytes[0] = 0xd1; bytes[1] = 0x0d;
  view.setUint32(4, 17, true);
  // 8 TiB, last chunk: far past the old total/block limits, exact as a JS number.
  view.setUint32(20, 2048, true);
  view.setUint32(8, 134217727, true);
  view.setUint16(44, 197, true);
  const frame = core.parseFrame(bytes);
  assert.ok(frame); assert.ok(frame.key.includes(":134217727:17"));
  const next = bytes.slice(); new DataView(next.buffer).setUint32(8, 134217726, true);
  assert.notEqual(core.parseFrame(next).key, frame.key);
  bytes[46] = 1; assert.equal(core.parseFrame(bytes), null); bytes[46] = 0;
  view.setUint32(8, 134217728, true); assert.equal(core.parseFrame(bytes), null);
  view.setUint32(8, 0, true); view.setUint16(44, 198, true); assert.equal(core.parseFrame(bytes), null);
});
