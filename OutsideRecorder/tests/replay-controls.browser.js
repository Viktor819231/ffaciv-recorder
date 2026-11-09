// Preview-only browser check, evaluated by tools/site/shot.mjs. Sends no real commands.
(() => {
  const check=(ok,message)=>{if(!ok)throw Error(message);};
  Mock.state.firstRun=false;
  Mock.state.current={...Mock.state.current,id:'test-current',context:'test-current',turn:37,ended:false,status:'recording',replay:{state:'recording',private:true,keepShort:false},upload:{state:'held'}};
  Bridge.send('previewRefresh');App.go('home');
  const card=()=>document.querySelector('.recorder-recordings .replay-state');
  const done=card().querySelector('[data-cmd="completeGame"]');
  check(done&&!done.disabled&&done.getClientRects().length,'Short game cannot finish');
  check(!card().querySelector('[data-cmd="replayChoice"]').checkVisibility(),'Visibility buttons should be hidden');
  check(!card().querySelector('[data-delete]').checkVisibility(),'Deletion should be hidden');
  card().querySelector('summary').click();
  for(let i=0;i<10;i++)Bridge.send('previewRefresh');
  check(card().querySelector('details').open,'Settings collapsed on state refresh');
  check(card().querySelector('[data-cmd="replayChoice"]').checkVisibility(),'Expanded visibility controls unavailable');
  card().querySelector('summary').click();
  let command;const original=Bridge.send;
  Bridge.send=(cmd,args)=>{command={cmd,args};};
  card().querySelector('[data-cmd="completeGame"]').click();
  check(command.cmd==='completeGame'&&command.args.context==='test-current','Wrong finishing command');
  Bridge.send=original;
  // Deliver a mock acknowledgement so the screenshot has no pending command banner.
  Bridge.send('completeGame');
  console.log('PASS: enabled short-game completion, collapsed replay controls, preserved expanded settings and correct finishing command.');
})();
