"""Local stand-in for the transcription API, so the end-to-end test sends no audio anywhere."""
import json, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = sys.argv[1]

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
    def _log(self, line):
        with open(LOG, 'a', encoding='utf-8') as f: f.write(line + '\n')
    def do_GET(self):
        self._log('GET ' + self.path + ' auth=' + str(self.headers.get('Authorization', '')[:11]))
        self._send({'data': [{'id': 'mock-asr'}, {'id': 'mock-llm'}]})
    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0)); body = self.rfile.read(n)
        if self.path.endswith('/audio/transcriptions'):
            from email.parser import BytesParser
            from email.policy import HTTP
            msg = BytesParser(policy=HTTP).parsebytes(b'Content-Type: ' + self.headers['Content-Type'].encode() + b'\r\n\r\n' + body)
            parts = {}
            for part in msg.iter_parts():
                name = part.get_param('name', header='content-disposition')
                if name == 'file':
                    data = part.get_payload(decode=True)
                    parts['file'] = {'filename': part.get_filename(), 'disposition': part['content-disposition'], 'riff': data[:4] == b'RIFF', 'bytes': len(data)}
                else:
                    parts[name] = part.get_payload(decode=True).decode('utf-8')[:60]
            self._log('ASR ' + json.dumps(parts, ensure_ascii=False))
            filename = (parts.get('file') or {}).get('filename') or ''
            if filename.rsplit('.', 1)[-1] not in ('flac', 'mp3', 'mp4', 'mpeg', 'mpga', 'm4a', 'ogg', 'opus', 'wav', 'webm'):
                return self._send({'error': {'message': 'file must be one of the following types: [flac mp3 mp4 mpeg mpga m4a ogg opus wav webm]'}}, 400)
            if 'model' not in parts:
                return self._send({'error': {'message': 'model is required'}}, 400)
            self._send({'text': 'eh bueno esta es una prueba de dictado con bercel', 'language': 'spanish'})
        elif self.path.endswith('/chat/completions'):
            req = json.loads(body)
            self._log('LLM model=%s user=%r' % (req.get('model'), req['messages'][-1]['content'][:80]))
            self._send({'choices': [{'message': {'content': 'Esta es una prueba de dictado con bercel.'}}]})
        else:
            self._send({'error': {'message': 'no'}}, 404)

ThreadingHTTPServer(('127.0.0.1', 5601), H).serve_forever()
