// DOS Canvas Renderer - JavaScript interop for Blazor WebAssembly
window.dosRenderer = {
    _ctx: null,
    _charWidth: 8,
    _charHeight: 16,

    // CGA 16-color palette
    _palette: [
        '#000000', '#0000AA', '#00AA00', '#00AAAA',
        '#AA0000', '#AA00AA', '#AA5500', '#AAAAAA',
        '#555555', '#5555FF', '#55FF55', '#55FFFF',
        '#FF5555', '#FF55FF', '#FFFF55', '#FFFFFF'
    ],

    init: function (canvasId) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;
        this._ctx = canvas.getContext('2d');
        this._ctx.imageSmoothingEnabled = false;
        this._ctx.font = this._charHeight + 'px "Courier New", monospace';
        this._ctx.textBaseline = 'top';
        this._ctx.fillStyle = '#000000';
        this._ctx.fillRect(0, 0, canvas.width, canvas.height);
    },

    renderScreen: function (canvasId, screenData, fgData, bgData, cursorCol, cursorRow, cursorVisible) {
        if (!this._ctx) this.init(canvasId);
        if (!this._ctx) return;

        const ctx = this._ctx;
        const cw = this._charWidth;
        const ch = this._charHeight;

        // Render each row
        for (let row = 0; row < screenData.length; row++) {
            const line = screenData[row];
            const fg = fgData[row];
            const bg = bgData[row];

            for (let col = 0; col < line.length; col++) {
                const x = col * cw;
                const y = row * ch;

                // Draw background
                ctx.fillStyle = this._palette[bg[col] & 0x0F];
                ctx.fillRect(x, y, cw, ch);

                // Draw character
                const c = line[col];
                if (c && c !== ' ') {
                    ctx.fillStyle = this._palette[fg[col] & 0x0F];
                    ctx.fillText(c, x, y);
                }
            }
        }

        // Draw cursor
        if (cursorVisible && cursorRow < screenData.length && cursorCol < 80) {
            ctx.fillStyle = '#FFFFFF';
            ctx.fillRect(cursorCol * cw, cursorRow * ch + ch - 2, cw, 2);
        }
    }
};
