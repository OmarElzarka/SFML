import { Injectable, NgZone } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

@Injectable({
  providedIn: 'root',
})
export class AudioService {
  private audioContext: AudioContext | null = null;
  private gainNode: GainNode | null = null;
  private socket: WebSocket | null = null;
  private nextPlayTime = 0;

  private _isConnected = new BehaviorSubject<boolean>(false);
  private _isMuted = new BehaviorSubject<boolean>(false);
  private _volume = new BehaviorSubject<number>(0.8);
  private _hasAudioSignal = new BehaviorSubject<boolean>(false);

  isConnected$ = this._isConnected.asObservable();
  isMuted$ = this._isMuted.asObservable();
  volume$ = this._volume.asObservable();
  hasAudioSignal$ = this._hasAudioSignal.asObservable();

  constructor(private ngZone: NgZone) {}

  private initAudioContext(): void {
    if (this.audioContext) return;

    try {
      const AudioContextClass =
        window.AudioContext ||
        (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
      this.audioContext = new AudioContextClass({ sampleRate: 44100 });
      this.gainNode = this.audioContext.createGain();
      this.gainNode.gain.setValueAtTime(
        this._isMuted.value ? 0 : this._volume.value,
        this.audioContext.currentTime
      );
      this.gainNode.connect(this.audioContext.destination);
    } catch (e) {
      console.warn('[AudioService] Web Audio API not supported in this browser.', e);
    }
  }

  /**
   * Resumes audio context on user interaction to comply with browser autoplay policies.
   */
  public resume(): void {
    if (this.audioContext && this.audioContext.state === 'suspended') {
      this.audioContext.resume().catch(() => {});
    }
  }

  /**
   * Connects to the WebSocket audio stream for the active session.
   * @param audioPath Relative or absolute audio websocket subpath (e.g. 'vnc/runtime/audio')
   */
  public connect(audioPath: string): void {
    this.disconnect();
    this.initAudioContext();
    this.resume();

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const cleanPath = audioPath.replace(/^\//, '');
    const wsUrl = `${protocol}//${window.location.host}/${cleanPath}`;

    try {
      this.socket = new WebSocket(wsUrl);
      this.socket.binaryType = 'arraybuffer';

      this.socket.onopen = () => {
        this.ngZone.run(() => {
          this._isConnected.next(true);
        });
      };

      this.socket.onmessage = (event: MessageEvent) => {
        if (event.data instanceof ArrayBuffer) {
          this.playPcmChunk(event.data);
        }
      };

      this.socket.onclose = () => {
        this.ngZone.run(() => {
          this._isConnected.next(false);
          this._hasAudioSignal.next(false);
        });
      };

      this.socket.onerror = (err) => {
        console.warn('[AudioService] Audio WebSocket error', err);
      };
    } catch (err) {
      console.error('[AudioService] Failed to establish audio websocket connection', err);
    }
  }

  /**
   * Decodes raw 16-bit stereo PCM chunk (44.1kHz) and schedules it smoothly on Web Audio timeline.
   */
  private playPcmChunk(buffer: ArrayBuffer): void {
    if (!this.audioContext || !this.gainNode) return;

    if (this.audioContext.state === 'suspended') {
      this.audioContext.resume().catch(() => {});
    }

    // Align to 4 bytes (2 channels * 2 bytes = 1 stereo frame = 4 bytes)
    const safeBytes = buffer.byteLength - (buffer.byteLength % 4);
    if (safeBytes === 0) return;

    const frameCount = safeBytes / 4;
    const int16 = new Int16Array(buffer, 0, safeBytes / 2);

    // Detect non-silent audio signal for UI activity indicator
    let isSilent = true;
    for (let i = 0; i < Math.min(int16.length, 200); i += 10) {
      if (Math.abs(int16[i]) > 100) {
        isSilent = false;
        break;
      }
    }
    if (!isSilent && !this._hasAudioSignal.value) {
      this.ngZone.run(() => this._hasAudioSignal.next(true));
    }

    const audioBuf = this.audioContext.createBuffer(2, frameCount, 44100);
    const left = audioBuf.getChannelData(0);
    const right = audioBuf.getChannelData(1);

    for (let i = 0; i < frameCount; i++) {
      left[i] = int16[i * 2] / 32768.0;
      right[i] = int16[i * 2 + 1] / 32768.0;
    }

    const source = this.audioContext.createBufferSource();
    source.buffer = audioBuf;
    source.connect(this.gainNode);

    const now = this.audioContext.currentTime;
    if (this.nextPlayTime < now || this.nextPlayTime > now + 0.15) {
      this.nextPlayTime = now + 0.025; // Small 25ms lead-in buffer, prevent lag buildup
    }

    source.start(this.nextPlayTime);
    this.nextPlayTime += audioBuf.duration;
  }

  public setVolume(vol: number): void {
    const clamped = Math.max(0, Math.min(1, vol));
    this._volume.next(clamped);
    if (this.gainNode && this.audioContext && !this._isMuted.value) {
      this.gainNode.gain.setValueAtTime(clamped, this.audioContext.currentTime);
    }
  }

  public toggleMute(): void {
    const nextMute = !this._isMuted.value;
    this._isMuted.next(nextMute);
    if (this.gainNode && this.audioContext) {
      this.gainNode.gain.setValueAtTime(
        nextMute ? 0 : this._volume.value,
        this.audioContext.currentTime
      );
    }
  }

  public disconnect(): void {
    if (this.socket) {
      try {
        this.socket.close();
      } catch {}
      this.socket = null;
    }
    this._isConnected.next(false);
    this._hasAudioSignal.next(false);
    this.nextPlayTime = 0;
  }
}
