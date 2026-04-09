(function() {
    var diag = document.createElement('div');
    diag.id = 'diag-bar';
    diag.style.cssText = 'position:fixed;bottom:0;left:0;right:0;background:#1a0000;color:#ff9999;font-size:11px;padding:4px 10px;z-index:9999;font-family:monospace;';
    document.body.appendChild(diag);

    function check() {
        var msgs = [];
        msgs.push('d3=' + (typeof d3 !== 'undefined' ? '✓' + d3.version : '✗ YOK'));
        msgs.push('es=' + (window._es !== undefined ? window._es : '✗ editor yüklenmedi'));
        if (window._jsErrors && window._jsErrors.length) msgs.push('jsErr=' + window._jsErrors.join(' | '));
        msgs.push('sim=' + (typeof updateEnvironment === 'function' ? '✓' : '✗ YOK'));

        
        // ★★★ YENİ: Ekran parlaklık değerlerini göster ★★★
        if (typeof window._lastEffectiveL !== 'undefined') {
            msgs.push('effL=' + Math.round(window._lastEffectiveL) + '%');
        }
        // if (typeof window._lastGammaT !== 'undefined') {
        //     msgs.push('t=' + (window._lastGammaT * 100).toFixed(0) + '%');
        // }
        if (typeof window._lastSCR !== 'undefined') {
            msgs.push('rgb=' + (window._lastSCR * 100).toFixed(0) + '%');
        }
        if (typeof window._lastFinalL !== 'undefined') {
            msgs.push('L=' + Math.round(window._lastFinalL) + '%');
        }

        
        var wrap = document.getElementById('d3-wrap');
        if (wrap) {
            var r = wrap.getBoundingClientRect();
            msgs.push('wrap=' + Math.round(r.width) + '×' + Math.round(r.height));
        } else {
            msgs.push('wrap=✗ BULUNAMADI');
        }
        try { msgs.push('WH=' + (typeof W === 'number' ? Math.round(W)+'×'+Math.round(H) : '✗')); } catch(e) { msgs.push('WH=✗'); }
        var svg = document.querySelector('#d3-wrap svg');
        msgs.push('svg=' + (svg ? svg.getAttribute('width') + '×' + svg.getAttribute('height') : '✗ YOK'));
        try { msgs.push('nodes=R' + nodes.R.length + '/G' + nodes.G.length + '/B' + nodes.B.length + '/L' + nodes.L.length); } catch(e) { msgs.push('nodes=✗'); }
        try { msgs.push('xSc=' + (typeof xSc === 'function' ? '✓' : '✗')); } catch(e) { msgs.push('xSc=✗'); }
        try { msgs.push('amb=' + (typeof currentAmbient === 'number' ? Math.round(currentAmbient) : '✗')); } catch(e) { msgs.push('amb=✗'); }

        var hasError = msgs.some(function(m) { return m.includes('✗'); });
        diag.style.background = hasError ? '#1a0000' : '#001a00';
        diag.style.color      = hasError ? '#ff9999'  : '#99ff99';
        diag.textContent = '🔍 ' + msgs.join('  |  ');
    }
    setInterval(check, 300);
})();
