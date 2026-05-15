class NeuralNetwork {
  constructor() {
    this.id = crypto.randomUUID();
    this._initWeights();
  }

  _initWeights() {
    const ni = 11, nh = 16, no = 4;
    const xavierH = Math.sqrt(2.0 / (ni + nh));
    const xavierO = Math.sqrt(2.0 / (nh + no));

    this.W1 = Array.from({length: nh}, () =>
      Array.from({length: ni}, () => (Math.random() * 2 - 1) * xavierH));
    this.b1 = new Array(nh).fill(0);
    this.W2 = Array.from({length: no}, () =>
      Array.from({length: nh}, () => (Math.random() * 2 - 1) * xavierO));
    this.b2 = new Array(no).fill(0);

    this._t = 0;

    this._m1W1 = Array.from({length: nh}, () => new Array(ni).fill(0));
    this._m2W1 = Array.from({length: nh}, () => new Array(ni).fill(0));
    this._m1b1 = new Array(nh).fill(0);
    this._m2b1 = new Array(nh).fill(0);
    this._m1W2 = Array.from({length: no}, () => new Array(nh).fill(0));
    this._m2W2 = Array.from({length: no}, () => new Array(nh).fill(0));
    this._m1b2 = new Array(no).fill(0);
    this._m2b2 = new Array(no).fill(0);
  }

  forward(inputs) {
    const nh = this.W1.length;
    const no = this.W2.length;

    const h = new Array(nh);
    for (let j = 0; j < nh; j++) {
      let s = this.b1[j];
      for (let i = 0; i < inputs.length; i++) s += this.W1[j][i] * inputs[i];
      h[j] = Math.tanh(s);
    }

    const out = new Array(no);
    for (let k = 0; k < no; k++) {
      let s = this.b2[k];
      for (let j = 0; j < nh; j++) s += this.W2[k][j] * h[j];
      out[k] = (1.0 / (1.0 + Math.exp(-s))) * 100.0;
    }

    return out;
  }

  train(samples, {epochs = 8000, lr = 0.01, lambda = 0.001} = {}) {
    const ni = 11, nh = this.W1.length, no = this.W2.length;
    const beta1 = 0.9, beta2 = 0.999, eps = 1e-8;

    let finalLoss = 0;

    for (let epoch = 0; epoch < epochs; epoch++) {
      let epochLoss = 0;

      for (let si = 0; si < samples.length; si++) {
        const {inputs, targets} = samples[si];
        const normTargets = targets.map(v => v / 100.0);

        const zH = new Array(nh);
        const h = new Array(nh);
        for (let j = 0; j < nh; j++) {
          let s = this.b1[j];
          for (let i = 0; i < ni; i++) s += this.W1[j][i] * inputs[i];
          zH[j] = s;
          h[j] = Math.tanh(s);
        }

        const zO = new Array(no);
        const aO = new Array(no);
        for (let k = 0; k < no; k++) {
          let s = this.b2[k];
          for (let j = 0; j < nh; j++) s += this.W2[k][j] * h[j];
          zO[k] = s;
          aO[k] = 1.0 / (1.0 + Math.exp(-s));
        }

        for (let k = 0; k < no; k++) {
          const diff = aO[k] - normTargets[k];
          epochLoss += diff * diff;
        }

        const dO = new Array(no);
        for (let k = 0; k < no; k++) {
          dO[k] = (aO[k] - normTargets[k]) * aO[k] * (1.0 - aO[k]);
        }

        const dH = new Array(nh).fill(0);
        for (let j = 0; j < nh; j++) {
          for (let k = 0; k < no; k++) {
            dH[j] += this.W2[k][j] * dO[k];
          }
          dH[j] *= (1.0 - h[j] * h[j]);
        }

        this._t++;
        const bc1 = 1.0 - Math.pow(beta1, this._t);
        const bc2 = 1.0 - Math.pow(beta2, this._t);

        for (let k = 0; k < no; k++) {
          for (let j = 0; j < nh; j++) {
            const g = dO[k] * h[j] + lambda * this.W2[k][j];
            this._m1W2[k][j] = beta1 * this._m1W2[k][j] + (1 - beta1) * g;
            this._m2W2[k][j] = beta2 * this._m2W2[k][j] + (1 - beta2) * g * g;
            const mHat = this._m1W2[k][j] / bc1;
            const vHat = this._m2W2[k][j] / bc2;
            this.W2[k][j] -= lr * mHat / (Math.sqrt(vHat) + eps);
          }
          const gb = dO[k];
          this._m1b2[k] = beta1 * this._m1b2[k] + (1 - beta1) * gb;
          this._m2b2[k] = beta2 * this._m2b2[k] + (1 - beta2) * gb * gb;
          this.b2[k] -= lr * (this._m1b2[k] / bc1) / (Math.sqrt(this._m2b2[k] / bc2) + eps);
        }

        for (let j = 0; j < nh; j++) {
          for (let i = 0; i < ni; i++) {
            const g = dH[j] * inputs[i] + lambda * this.W1[j][i];
            this._m1W1[j][i] = beta1 * this._m1W1[j][i] + (1 - beta1) * g;
            this._m2W1[j][i] = beta2 * this._m2W1[j][i] + (1 - beta2) * g * g;
            const mHat = this._m1W1[j][i] / bc1;
            const vHat = this._m2W1[j][i] / bc2;
            this.W1[j][i] -= lr * mHat / (Math.sqrt(vHat) + eps);
          }
          const gb = dH[j];
          this._m1b1[j] = beta1 * this._m1b1[j] + (1 - beta1) * gb;
          this._m2b1[j] = beta2 * this._m2b1[j] + (1 - beta2) * gb * gb;
          this.b1[j] -= lr * (this._m1b1[j] / bc1) / (Math.sqrt(this._m2b1[j] / bc2) + eps);
        }
      }

      finalLoss = epochLoss / (samples.length * no);
    }

    let finalMse = 0;
    for (let si = 0; si < samples.length; si++) {
      const pred = this.forward(samples[si].inputs);
      for (let k = 0; k < no; k++) {
        const d = pred[k] - samples[si].targets[k];
        finalMse += d * d;
      }
    }
    finalMse /= (samples.length * no);

    return finalMse;
  }

  toJSON() {
    return {
      id: this.id,
      arch: [11, 16, 4],
      W1: this.W1,
      b1: this.b1,
      W2: this.W2,
      b2: this.b2,
      _t: this._t,
      _m1W1: this._m1W1,
      _m2W1: this._m2W1,
      _m1b1: this._m1b1,
      _m2b1: this._m2b1,
      _m1W2: this._m1W2,
      _m2W2: this._m2W2,
      _m1b2: this._m1b2,
      _m2b2: this._m2b2,
    };
  }

  fromJSON(data) {
    this.id   = data.id   ?? crypto.randomUUID();
    this.W1   = data.W1;
    this.b1   = data.b1;
    this.W2   = data.W2;
    this.b2   = data.b2;
    this._t   = data._t   ?? 0;
    const nh  = this.W1.length;
    const ni  = this.W1[0].length;
    const no  = this.W2.length;

    this._m1W1 = data._m1W1 ?? Array.from({length: nh}, () => new Array(ni).fill(0));
    this._m2W1 = data._m2W1 ?? Array.from({length: nh}, () => new Array(ni).fill(0));
    this._m1b1 = data._m1b1 ?? new Array(nh).fill(0);
    this._m2b1 = data._m2b1 ?? new Array(nh).fill(0);
    this._m1W2 = data._m1W2 ?? Array.from({length: no}, () => new Array(nh).fill(0));
    this._m2W2 = data._m2W2 ?? Array.from({length: no}, () => new Array(nh).fill(0));
    this._m1b2 = data._m1b2 ?? new Array(no).fill(0);
    this._m2b2 = data._m2b2 ?? new Array(no).fill(0);
    return this;
  }

  clone() {
    const c = new NeuralNetwork();
    return c.fromJSON(JSON.parse(JSON.stringify(this.toJSON())));
  }
}

function encodeInputs({lat, lon, doy, hour, procR, procG, procB, ambientPct}) {
  const arr = new Float64Array(11);
  arr[0]  = lat / 90.0;
  arr[1]  = Math.sin(2 * Math.PI * lon / 360.0);
  arr[2]  = Math.cos(2 * Math.PI * lon / 360.0);
  arr[3]  = Math.sin(2 * Math.PI * doy / 365.0);
  arr[4]  = Math.cos(2 * Math.PI * doy / 365.0);
  arr[5]  = Math.sin(2 * Math.PI * hour / 24.0);
  arr[6]  = Math.cos(2 * Math.PI * hour / 24.0);
  arr[7]  = procR / 100.0;
  arr[8]  = procG / 100.0;
  arr[9]  = procB / 100.0;
  arr[10] = ambientPct / 100.0;
  return arr;
}

window.NeuralNetwork = NeuralNetwork;
window.encodeInputs  = encodeInputs;
