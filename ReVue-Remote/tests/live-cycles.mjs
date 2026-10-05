import {spawn} from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
// Requires Node 22+, FFmpeg, MediaMTX, Chrome, and a built ReVue-Remote Debug server.
// Uses real RTMP/HLS and the production viewer/VRO HLS page. The local proxy
// substitutes for the Windows app server; it does not exercise WebView2 itself.
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'revue-live-cycles-'));
const dotnet=process.env.DOTNET_BINARY||'dotnet';
const ffmpeg=process.env.FFMPEG_BINARY||'ffmpeg';
const mediamtx=process.env.MEDIAMTX_BINARY||'mediamtx';
const chrome=process.env.CHROME_BINARY||(process.platform==='darwin'
  ? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' : 'chromium');
const offlineOnly=process.argv.includes('--offline');
const controlGap=process.argv.includes('--control-gap');
const storageChurn=process.argv.includes('--storage-churn');
fs.mkdirSync(tmp+'/data/_system',{recursive:true});
fs.writeFileSync(tmp+'/data/_system/keys.json',JSON.stringify([{code:'LIVE01',createdByUserId:'integration',mode:'Live',publishPasswordHash:''}]));
fs.writeFileSync(tmp+'/mediamtx.yml',`logLevel: warn
rtmp: yes
rtmpAddress: 127.0.0.1:11938
rtsp: no
hls: yes
hlsAddress: 127.0.0.1:18898
hlsVariant: fmp4
hlsSegmentDuration: 2s
hlsSegmentCount: 20
hlsAlwaysRemux: yes
webrtc: no
srt: no
paths:
  all_others:
    source: publisher
`);
const profile=tmp+'/chrome-'+Date.now();
const children=[];
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function launch(exe,args,name,extra={}) {
  const child=spawn(exe,args,{...extra,stdio:['ignore',fs.openSync(`${tmp}/${name}.log`,'w'),fs.openSync(`${tmp}/${name}.err`,'w')]});
  child.on('error',error=>console.error(name,error.message));
  children.push(child); return child;
}
async function until(fn,timeout=20000) {
  const end=Date.now()+timeout;
  while(Date.now()<end){try{const v=await fn();if(v)return v;}catch{} await sleep(100);}
  throw Error('Timed out waiting for readiness');
}
let eventId='', seq=0, state={mode:'ready',videoId:'',positionSeconds:0,timelinePositionSeconds:0};
const operatorInstanceId=crypto.randomUUID().replaceAll('-',''), operatorGeneration=Date.now();
async function publish(patch={}) {
  state={...state,...patch};
  const response=await fetch('http://127.0.0.1:15098/api/sessions/LIVE01/playback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sourceType:'Live',operatorInstanceId,operatorGeneration,operatorSequence:++seq,timelineDurationSeconds:120,playbackRate:1,liveDelaySeconds:5,isPlaying:state.mode==='recording',...state})});
  if(!response.ok)throw Error(await response.text());
  return response.json();
}
let heartbeat, churn;
const proxy=http.createServer(async(req,res)=>{
  try {
    const url=new URL(req.url,'http://127.0.0.1:18098');
    if(url.pathname==='/'){
      res.setHeader('Content-Type','text/html');
      res.end('<iframe id="player" width="640" height="360" src="/remote-live-hls.html"></iframe>');return;
    }
    if(['/remote-live-hls.html','/hls.min.js'].includes(url.pathname)){
      res.setHeader('Content-Type',url.pathname.endsWith('.html')?'text/html':'text/javascript');
      res.end(fs.readFileSync(path.join(root,'ReVue-VRO/wwwroot',url.pathname)));return;
    }
    let remotePath;
    if(url.pathname.startsWith('/api/remote-live-preview/'))remotePath=url.pathname.replace('/api/remote-live-preview/','/api/sessions/LIVE01/live/preview/');
    else if(url.pathname.startsWith('/api/remote-live-event/')){
      const pieces=url.pathname.split('/');
      if(pieces[3]!==eventId){res.writeHead(404);res.end();return;}
      remotePath=url.pathname.replace('/api/remote-live-event/','/api/sessions/LIVE01/live/events/');
    }else {res.writeHead(404);res.end();return;}
    const upstream=await fetch('http://127.0.0.1:15098'+remotePath+url.search);
    res.writeHead(upstream.status,{'Content-Type':upstream.headers.get('Content-Type')||'video/mp4','Cache-Control':url.pathname.includes('preview')||url.pathname.endsWith('.m3u8')?'no-store':'public, max-age=86400'});
    if(url.pathname.endsWith('.m3u8')){
      res.end((await upstream.text()).replaceAll('/api/sessions/LIVE01/live/preview/','/api/remote-live-preview/'));
    }else res.end(Buffer.from(await upstream.arrayBuffer()));
  }catch(e){if(!res.headersSent)res.writeHead(503);res.end();}
});
class CDP {
  constructor(ws){this.ws=ws;this.pending=new Map;this.id=0;ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id){const p=this.pending.get(m.id);this.pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}});}
  send(method,params={}){const id=++this.id;return new Promise((resolve,reject)=>{this.pending.set(id,{resolve,reject});this.ws.send(JSON.stringify({id,method,params}));});}
  async eval(expression){const r=await this.send('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
}
async function tab(base,url){const t=await(await fetch(base+'/json/new?'+encodeURIComponent(url),{method:'PUT'})).json();const ws=new WebSocket(t.webSocketDebuggerUrl);await new Promise(r=>ws.addEventListener('open',r,{once:true}));return new CDP(ws);}
function sampleExpr(vro){return `(()=>{const d=${vro?'document.getElementById("player").contentDocument':'document'},w=${vro?'document.getElementById("player").contentWindow':'window'};const v=${vro?'(w.getLiveDiagnostics().source==="event"?d.getElementById("eventVideo"):d.getElementById("liveVideo"))':'d.getElementById("video")'};if(!v||v.readyState<2)return null;const c=d.createElement('canvas');c.width=1;c.height=1;c.getContext('2d').drawImage(v,0,0,1,1);const rgb=[...c.getContext('2d').getImageData(0,0,1,1).data].slice(0,3);let best=1e9,sec=0;for(let t=0;t<220;t++){let diff=[7,13,19].reduce((sum,k,i)=>sum+(rgb[i]-(16+t*k%220))**2,0);if(diff<best){best=diff;sec=t;}}return {sec,rgb,position:v.currentTime,paused:v.paused,seeking:v.seeking,${vro?'diagnostics:w.getLiveDiagnostics()':'url:v.currentSrc'}};})()`;}
async function checkUncontrolledViewer(viewer,label) {
  await until(()=>viewer.eval(`document.getElementById('video').readyState>=2 &&
    !document.getElementById('video').paused &&
    document.getElementById('vroConnectionStatus').textContent==='VRO Offline'`));
  for(const role of ['technical-controller','judging','referee','announcer','data-specialist']) {
    await viewer.eval(`document.querySelector('[data-panel-type="${role}"]').click()`);
    await until(()=>viewer.eval(`document.getElementById('emptyState').classList.contains('hidden') &&
      document.getElementById('reviewIndicator').classList.contains('hidden') &&
      document.getElementById('timelineControlRow').hidden && !document.getElementById('video').paused`));
    const before=await viewer.eval(sampleExpr(false));
    await sleep(1500);
    const after=await viewer.eval(sampleExpr(false));
    assert(after.sec>before.sec,`${label}/${role}: video is frozen`);
  }
  await viewer.eval(`document.querySelector('[data-panel-type="technical-controller"]').click()`);
  console.log('PASS:',label,'shows moving live video for technical, Judge, Referee, Announcer, Data Specialist');
}
try {
  // Refuse to attach to an unrelated service already using a fixture port.
  for(const port of [11938,18898,15098,18098]) {
    const probe=http.createServer();
    await new Promise((resolve,reject)=>probe.once('error',reject).listen(port,'127.0.0.1',resolve));
    await new Promise(resolve=>probe.close(resolve));
  }
  console.log('Test artifacts:',tmp);
  launch(mediamtx,[tmp+'/mediamtx.yml'],'mediamtx');
  launch(dotnet,[root+'/ReVue-Remote/bin/Debug/net10.0/ReVue-Remote.dll'],'remote',{cwd:root+'/ReVue-Remote',env:{...process.env,ASPNETCORE_URLS:'http://127.0.0.1:15098',ReVueRemote__StorageRoot:tmp+'/data',ReVueRemote__MediaMtxHlsBase:'http://127.0.0.1:18898',ReVueRemote__FfmpegPath:ffmpeg}});
  await until(async()=> (await fetch('http://127.0.0.1:15098/api/health')).ok);
  launch(ffmpeg,['-hide_banner','-loglevel','warning','-re','-f','lavfi','-i',"nullsrc=size=320x180:rate=60000/1001,format=rgb24,geq=r='16+mod(floor(N/60)*7,220)':g='16+mod(floor(N/60)*13,220)':b='16+mod(floor(N/60)*19,220)'",'-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-c:v','libx264','-pix_fmt','yuv420p','-preset','ultrafast','-g','60','-bf','0','-sc_threshold','0','-c:a','aac','-f','flv','rtmp://127.0.0.1:11938/LIVE01'],'publisher');
  await new Promise(r=>proxy.listen(18098,'127.0.0.1',r));
  launch(chrome,['--headless=new','--remote-debugging-port=0',`--user-data-dir=${profile}`,'--no-first-run','--autoplay-policy=no-user-gesture-required','--disable-background-timer-throttling','--disable-renderer-backgrounding','about:blank'],'chrome');
  const debug=await until(()=>fs.existsSync(profile+'/DevToolsActivePort')&&fs.readFileSync(profile+'/DevToolsActivePort','utf8').split('\n')[0]);
  const base='http://127.0.0.1:'+debug;
  const vro=await tab(base,'http://127.0.0.1:18098/');
  const viewer=await tab(base,'http://127.0.0.1:15098/?session=LIVE01');
  await vro.send('Emulation.setFocusEmulationEnabled',{enabled:true});
  await viewer.send('Emulation.setFocusEmulationEnabled',{enabled:true});
  await vro.send('Page.bringToFront');
  await until(()=>vro.eval('document.getElementById("player").contentWindow.isPreviewReadyForNext?.()'),30000);
  await until(()=>viewer.eval('document.getElementById("video")?.readyState>=2'),30000);
  console.log('initial',await vro.eval(sampleExpr(true)),await viewer.eval(sampleExpr(false)));
  if(offlineOnly) await checkUncontrolledViewer(viewer,'join before any VRO');
  await publish();heartbeat=setInterval(()=>publish().catch(e=>console.error('heartbeat',e)),2000);
  if(storageChurn) {
    const directory=tmp+'/data/LIVE01/live/storage-measurement-race';
    churn=setInterval(()=>{
      fs.rmSync(directory,{recursive:true,force:true});
      for(let n=0;n<20;n++) {
        fs.mkdirSync(`${directory}/${n}`,{recursive:true});
        fs.writeFileSync(`${directory}/${n}/sample.mp4`,Buffer.alloc(1024));
      }
    },25);
  }
  const results=[];
  for(let cycle=1;cycle<=(offlineOnly?1:5);cycle++){
    const before=await vro.eval(sampleExpr(true));
    eventId=crypto.randomUUID().replaceAll('-','');
    await publish({mode:'preparing',videoId:eventId});
    await until(async()=>{const p=await fetch(`http://127.0.0.1:15098/api/sessions/LIVE01/live/events/${eventId}/index.m3u8`);return p.ok&&(await p.text()).includes('.m4s');});
    await sleep(1500); // Actual local recorder arms while preview continues.
    await publish({mode:'recording'});
    await vro.eval(`document.getElementById('player').contentWindow.useRecordingEvent('${eventId}')`);
    await until(()=>vro.eval('document.getElementById("player").contentWindow.getRecordingPositionSeconds?.()>0'));
    const started=await vro.eval(sampleExpr(true));
    assert(started.sec>=before.sec-3,`cycle ${cycle}: Record rewound ${before.sec-started.sec}s`);
    let previous=started.sec;
    for(let n=0;n<7;n++){
      await sleep(1000);const sample=await vro.eval(sampleExpr(true));
      assert(sample.sec>=previous,`cycle ${cycle}: backwards video during Record`);
      assert(sample.sec<=previous+2,`cycle ${cycle}: forward jump during Record`);previous=sample.sec;
    }
    if(controlGap && cycle===3) {
      clearInterval(heartbeat);heartbeat=null;
      const beforeGap=await vro.eval(sampleExpr(true));
      for(let second=0;second<65;second++) {
        await sleep(1000);
        const sample=await vro.eval(sampleExpr(true));
        assert(sample.sec>=previous && sample.sec<=previous+2,'video skipped footage during a control interruption');
        previous=sample.sec;
        if(second===10) assert.equal(await viewer.eval(`document.getElementById('vroConnectionStatus').textContent`),'VRO Offline',
          'connection metric should expire while the recording continues');
      }
      const playlist=await (await fetch(`http://127.0.0.1:15098/api/sessions/LIVE01/live/events/${eventId}/index.m3u8`)).text();
      assert(!playlist.includes('#EXT-X-ENDLIST'),'control heartbeat interruption terminated the recording producer');
      const afterGap=await vro.eval(sampleExpr(true));
      assert(afterGap.sec>=beforeGap.sec+60,'VRO froze during a control interruption');
      await viewer.send('Page.reload');
      await until(()=>viewer.eval(`document.getElementById('video').readyState>=2 &&
        !document.getElementById('video').paused && document.getElementById('emptyState').classList.contains('hidden')`));
      await publish();heartbeat=setInterval(()=>publish().catch(e=>console.error('heartbeat',e)),2000);
      console.log('PASS: 65-second control interruption leaves recording and refreshed viewer playing without skips');
    }
    const clip=await vro.eval(sampleExpr(true));
    await viewer.send('Page.reload');
    await vro.eval("document.getElementById('player').style.visibility=''");
    await until(()=>viewer.eval('document.getElementById("video")?.readyState>=2'));
    const joined=await viewer.eval(sampleExpr(false));
    const current=await vro.eval(sampleExpr(true));
    assert(Math.abs(joined.sec-current.sec)<=4,`cycle ${cycle}: refresh is ${current.sec-joined.sec}s behind`);
    const duration=(await vro.eval(sampleExpr(true))).position;
    await publish({mode:'replay',isPlaying:false,timelineDurationSeconds:duration,positionSeconds:clip.position,timelinePositionSeconds:clip.position});
    await sleep(1500);
    await vro.eval(`(()=>{const w=document.getElementById('player').contentWindow,v=w.document.getElementById('eventVideo');v.pause();v.currentTime=${clip.position};})()`);
    await sleep(600);
    const replay=await vro.eval(sampleExpr(true));
    assert(Math.abs(clip.sec-replay.sec)<=1,`cycle ${cycle}: clip points to wrong footage`);
    if(offlineOnly) {
      await until(()=>viewer.eval(`document.getElementById('video').paused &&
        document.getElementById('reviewIndicator').classList.contains('review')`));
      const prefix=`/api/sessions/LIVE01/live/events/${eventId}/`;
      const cachedCount=()=>viewer.eval(`(async()=> (await (await caches.open('revue-live-events-v1')).keys())
        .filter(r=>new URL(r.url).pathname.startsWith('${prefix}')).length)()`);
      const beforeOffline=await cachedCount();
      assert(beforeOffline>0,'recorded media was not cached');
      clearInterval(heartbeat);heartbeat=null;
      await sleep(7500); // Let the real server expire the operator lease.
      await checkUncontrolledViewer(viewer,'existing viewer after VRO disconnects');
      assert((await cachedCount())>=beforeOffline,'operator disconnect purged the recording cache');
      const newcomer=await tab(base,'http://127.0.0.1:15098/?session=LIVE01');
      await checkUncontrolledViewer(newcomer,'new viewer with an old recording on the server');
      await viewer.send('Page.reload');
      await checkUncontrolledViewer(viewer,'refreshed viewer while VRO is offline');
      // Reconnection must still restore operator-directed replay.
      await publish();heartbeat=setInterval(()=>publish().catch(e=>console.error('heartbeat',e)),2000);
      await until(()=>viewer.eval(`document.getElementById('video').paused &&
        document.getElementById('reviewIndicator').classList.contains('review') &&
        document.getElementById('vroConnectionStatus').textContent==='VRO Online'`));
      console.log('PASS: cached recording retained and operator replay restored on reconnection');
      break;
    }
    const ended=await vro.eval(sampleExpr(true));
    await publish({mode:'ready',videoId:'',positionSeconds:0,timelinePositionSeconds:0});eventId='';
    if(controlGap) {
      const rejectedEvent=crypto.randomUUID().replaceAll('-','');
      const response=await fetch('http://127.0.0.1:15098/api/sessions/LIVE01/playback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sourceType:'Live',operatorInstanceId,operatorGeneration,operatorSequence:1,videoId:rejectedEvent,mode:'preparing',positionSeconds:0,timelinePositionSeconds:0,timelineDurationSeconds:0,playbackRate:1})});
      assert(response.ok,'stale command should return current state');
      assert(!fs.existsSync(`${tmp}/data/LIVE01/live/${rejectedEvent}`),'rejected command started a recording process');
    }
    await vro.eval(`(()=>{const old=document.getElementById('player'),fresh=old.cloneNode(false);fresh.style.visibility='hidden';fresh.src='/remote-live-hls.html?ts='+Date.now();old.replaceWith(fresh);})()`);
    await until(()=>vro.eval('document.getElementById("player").contentWindow.isPreviewReadyForNext?.()'),30000);
    await vro.eval("document.getElementById('player').style.visibility=''");
    await until(()=>viewer.eval('document.getElementById("video")?.readyState>=2&&!document.getElementById("video").paused'));
    const next=await vro.eval(sampleExpr(true)),remote=await viewer.eval(sampleExpr(false));
    assert(next.sec>=ended.sec,`cycle ${cycle}: Next shows old competitor`);
    assert(Math.abs(next.sec-remote.sec)<=4,`cycle ${cycle}: Next is ${remote.sec-next.sec}s behind viewer`);
    results.push({cycle,recordRollbackSeconds:before.sec-started.sec,refreshDifferenceSeconds:current.sec-joined.sec,nextDifferenceSeconds:remote.sec-next.sec,clip:clip.sec,replay:replay.sec});
    console.log(JSON.stringify(results.at(-1)));
  }
  fs.writeFileSync(tmp+'/results.json',JSON.stringify(results,null,2));
  console.log(offlineOnly?'PASS: uncontrolled live feeds and operator reconnection':'PASS: five real-media competitor cycles, refresh during Record, no skips, clip replay');
}finally{
  clearInterval(heartbeat);clearInterval(churn);proxy.close();for(const child of children.reverse())child.kill('SIGTERM');
}
