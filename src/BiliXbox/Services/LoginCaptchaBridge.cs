using System;
using System.Text.RegularExpressions;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    // A one-use bridge for the human-operated Geetest widget. The passport server
    // still verifies the proof; this only rejects stale/malformed script messages.
    internal sealed class LoginCaptchaBridge
    {
        private readonly string _nonce, _challenge;
        private bool _completed;
        internal LoginCaptchaBridge(string nonce, string challenge)
        {
            if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(challenge))
                throw new ArgumentException("安全验证参数不完整。");
            _nonce = nonce; _challenge = challenge;
        }

        internal bool TryRead(string json, out string state, out LoginCaptchaProof proof)
        {
            state = null; proof = null;
            if (_completed || string.IsNullOrEmpty(json) || json.Length > 4096) return false;
            JObject message;
            try { message = JObject.Parse(json); }
            catch (JsonException) { return false; }
            if (!string.Equals(ReadString(message, "nonce"), _nonce, StringComparison.Ordinal)) return false;
            var kind = ReadString(message, "kind");
            if (kind == "ready" || kind == "error" || kind == "cancel") { state = kind; return true; }
            if (kind != "success") return false;
            var challenge = ReadString(message, "challenge");
            var validate = ReadString(message, "validate");
            var seccode = ReadString(message, "seccode");
            if (!ValidToken(challenge, 256) || !ValidToken(validate, 256)
                || seccode != validate + "|jordan") return false;
            bool same = string.Equals(challenge, _challenge, StringComparison.Ordinal);
            bool extended = _challenge.Length == 32 && challenge.Length == 34
                && challenge.StartsWith(_challenge, StringComparison.Ordinal)
                && Regex.IsMatch(challenge.Substring(32), "\\A[0-9a-fA-F]{2}\\z");
            if (!same && !extended) return false;
            proof = new LoginCaptchaProof { Challenge = challenge, Validate = validate, Seccode = seccode };
            state = "success"; _completed = true; return true;
        }

        private static bool ValidToken(string value, int maximum) => !string.IsNullOrEmpty(value)
            && value.Length <= maximum && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]+\\z");
        private static string ReadString(JObject message, string key) => message[key]?.Type == JTokenType.String ? message[key].Value<string>() : null;

        internal static string BuildPage(string gt, string challenge, string nonce)
        {
            var settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string config = JsonConvert.SerializeObject(new { gt, challenge, nonce }, settings);
            // SDK and challenge assets come directly from the verification provider.
            // No username, password, phone, SMS code or session cookie enters this page.
            return "<!doctype html><html><head><meta charset='utf-8'>"
                + "<meta name='viewport' content='width=device-width,initial-scale=1'>"
                + "<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; "
                + "script-src 'unsafe-inline' 'unsafe-eval' https://*.geetest.com https://*.geevisit.com https://*.geetest.cn; "
                + "style-src 'unsafe-inline' https://*.geetest.com https://*.geevisit.com https://*.geetest.cn; "
                + "img-src data: blob: https://*.geetest.com https://*.geevisit.com https://*.geetest.cn; "
                + "connect-src https://*.geetest.com https://*.geevisit.com https://*.geetest.cn; "
                + "font-src data: https://*.geetest.com https://*.geevisit.com; frame-src https://*.geetest.com https://*.geevisit.com;\">"
                + "<style>html,body{margin:0;width:100%;height:100%;background:#1c1d23;color:#f2f2f7;font-family:Segoe UI,sans-serif;}"
                + "body{overflow:auto;}#status{position:absolute;left:16px;top:8px;font-size:15px;line-height:1.5;}"
                + "#challenge-area{position:relative;width:100%;min-height:480px;}"
                + "[data-tv-captcha]:focus{outline:3px solid #fff!important;outline-offset:-3px!important;}"
                + "</style></head><body><p id='status'>正在加载 B 站安全验证…</p><div id='challenge-area'></div><script>"
                + "(function(){'use strict';var c=" + config + ";"
                + PageScript
                + "</script></body></html>";
        }

        // Only adapts the provider's presentation and manual keyboard input.
        // All challenge selection remains human operated; getValidate() is the
        // sole source of proof, and the passport service must still accept it.
        private const string PageScript = @"
var done=false, observer=null, focusQueued=false;
var area=document.getElementById('challenge-area'), status=document.getElementById('status');
var viewportWidth=Math.max(210,window.innerWidth||document.documentElement.clientWidth||320);
var viewportHeight=Math.max(210,window.innerHeight||document.documentElement.clientHeight||320);
// Keep a positive top margin even for SDK variants with fixed instructions and
// footer heights. Extra content can scroll instead of being centered above y=0.
area.style.height=Math.max(480,viewportHeight)+'px';
var challengeWidth=Math.max(210,Math.floor(Math.min(420,viewportWidth-24,(viewportHeight-24)*0.68)));
function report(kind,v){
    if(done)return;
    var m={nonce:c.nonce,kind:kind};
    if(v){m.challenge=v.geetest_challenge;m.validate=v.geetest_validate;m.seccode=v.geetest_seccode;}
    try{window.external.notify(JSON.stringify(m));}catch(e){}
    if(kind==='success'||kind==='cancel'||kind==='error')done=true;
}
function failed(){status.style.display='block';status.textContent='验证服务暂时不可用，请返回后重试。';report('error');}
function visible(el){
    var r=el.getBoundingClientRect(), s=window.getComputedStyle(el);
    return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'&&!el.disabled;
}
function controls(){
    // SDK versions may mount the popup under body even when area controls its
    // positioning, so the generated verification document is the search scope.
    var nodes=document.querySelectorAll('.geetest_item:not(.geetest_big_item),.geetest_commit,.geetest_refresh,.geetest_close,.geetest_radar_btn,.geetest_btn,button,[role=button]');
    var result=[];
    for(var i=0;i<nodes.length;i++){
        var el=nodes[i];if(!visible(el))continue;
        el.setAttribute('tabindex','0');el.setAttribute('data-tv-captcha','1');
        result.push(el);
    }
    return result;
}
function focusControl(el){
    if(!el)return;
    el.focus();
    // The document may scroll on a small viewport. Focusing the provider's
    // actual confirm control must reveal its entire bounds before activation.
    try{el.scrollIntoView({block:'nearest',inline:'nearest'});}catch(e){el.scrollIntoView(false);}
}
function hasClass(el,name){return (' '+el.className+' ').indexOf(' '+name+' ')>=0;}
function firstControl(list){
    for(var i=0;i<list.length;i++)if(hasClass(list[i],'geetest_item'))return list[i];
    for(var j=0;j<list.length;j++)if(hasClass(list[j],'geetest_commit'))return list[j];
    return list[0];
}
function ensureFocus(){
    if(done)return;var list=controls();
    // A word/icon point challenge must start with its instructions and picture
    // visible. Never scroll straight to a footer control while it loads.
    if(list.length&&list.indexOf(document.activeElement)<0){
        var first=firstControl(list);if(hasClass(first,'geetest_item'))focusControl(first);
    }
}
function queueFocus(){
    if(focusQueued||done)return;focusQueued=true;
    window.requestAnimationFrame(function(){focusQueued=false;ensureFocus();});
}
window.biliCaptchaFocus=ensureFocus;
var held=0,heldSince=0,lastMove=0;
document.addEventListener('keyup',function(){held=0;});
document.addEventListener('keydown',function(e){
    var key=e.keyCode||e.which;
    if(key===27||key===196||key===143||key===166||e.key==='GoBack'||e.key==='BrowserBack'){e.preventDefault();e.stopPropagation();report('cancel');return;}
    if(done)return;
    var direction=key===37||key===205||key===140?'left':key===38||key===203||key===138?'up':key===39||key===206||key===141?'right':key===40||key===204||key===139?'down':null;
    var accept=key===13||key===32||key===195||key===142;
    if(!direction&&!accept)return;
    var list=controls(), current=document.activeElement;
    if(!list.length)return;
    e.preventDefault();e.stopPropagation();
    if(list.indexOf(current)<0){focusControl(firstControl(list));return;}
    var now=Date.now();
    if(accept){
        if(held===key||e.repeat)return;
        held=key;heldSince=now;lastMove=now;current.click();return;
    }
    if(held===key){if(now-heldSince<450||now-lastMove<180)return;}
    else{held=key;heldSince=now;}
    lastMove=now;
    var origin=current.getBoundingClientRect(), ox=origin.left+origin.width/2, oy=origin.top+origin.height/2;
    var next=null, best=Infinity;
    for(var i=0;i<list.length;i++){
        if(list[i]===current)continue;
        var r=list[i].getBoundingClientRect(), dx=r.left+r.width/2-ox, dy=r.top+r.height/2-oy;
        var forward=direction==='left'?-dx:direction==='right'?dx:direction==='up'?-dy:dy;
        if(forward<=2)continue;
        var cross=direction==='left'||direction==='right'?Math.abs(dy):Math.abs(dx);
        var score=forward+cross*3;
        if(score<best){best=score;next=list[i];}
    }
    if(next)focusControl(next);
},true);
var s=document.createElement('script');s.src='https://static.geetest.com/static/tools/gt.js';
s.onerror=failed;s.onload=function(){
    if(typeof initGeetest!=='function'){failed();return;}
    initGeetest({gt:c.gt,challenge:c.challenge,offline:false,new_captcha:true,product:'bind',area:'#challenge-area',next_width:challengeWidth+'px',https:true,lang:'zh-cn'},function(widget){
        // Bind mode deliberately has no appendTo button. onReady -> verify is
        // the provider's documented way to show the complete challenge here.
        widget.onReady(function(){status.style.display='none';report('ready');widget.verify();queueFocus();});
        widget.onSuccess(function(){var proof=widget.getValidate();if(proof)report('success',proof);});
        widget.onError(failed);widget.onClose(function(){report('cancel');});
        if(window.MutationObserver){observer=new MutationObserver(queueFocus);observer.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['style','class']});}
    });
};
document.head.appendChild(s);
window.addEventListener('pagehide',function(){done=true;if(observer)observer.disconnect();});
})();";
    }
}
