package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"io"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
)

var (
	target     string
	portDirect string
	portInject string
)

func init() {
	flag.StringVar(&target, "target", "http://localhost:8081", "llama-server base URL")
	flag.StringVar(&portDirect, "port-direct", "8082", "port direct (Open WebUI)")
	flag.StringVar(&portInject, "port-inject", "8083", "port inject (OpenHands)")
}

// injectNoThinking lit le JSON token par token.
// Les champs autres que "chat_template_kwargs" sont copiés via RawMessage
// (pas de décodage/réencodage — les bytes bruts sont préservés).
// "messages" avec des PDFs en base64 passe donc en O(1) mémoire.
// Seul chat_template_kwargs est décodé (petit objet).
// Retourne le body modifié et sa taille exacte.
func injectNoThinking(r io.Reader) ([]byte, error) {
	var buf bytes.Buffer
	dec := json.NewDecoder(r)
	dec.UseNumber()

	// Objet racine {
	tok, err := dec.Token()
	if err != nil {
		return nil, err
	}
	if delim, ok := tok.(json.Delim); !ok || delim != '{' {
		return nil, nil
	}

	buf.WriteByte('{')
	first := true
	kwargsInjected := false

	for dec.More() {
		keyTok, err := dec.Token()
		if err != nil {
			return nil, err
		}
		key, ok := keyTok.(string)
		if !ok {
			return nil, nil
		}

		if !first {
			buf.WriteByte(',')
		}
		first = false

		keyBytes, _ := json.Marshal(key)
		buf.Write(keyBytes)
		buf.WriteByte(':')

		if key == "chat_template_kwargs" {
			var existing map[string]interface{}
			if err := dec.Decode(&existing); err != nil {
				return nil, err
			}
			if existing == nil {
				existing = make(map[string]interface{})
			}
			existing["enable_thinking"] = false
			valBytes, err := json.Marshal(existing)
			if err != nil {
				return nil, err
			}
			buf.Write(valBytes)
			kwargsInjected = true
		} else {
			// RawMessage : les bytes bruts sont copiés sans décodage.
			// Un champ "messages" avec 50MB de base64 est copié tel quel.
			var raw json.RawMessage
			if err := dec.Decode(&raw); err != nil {
				return nil, err
			}
			buf.Write(raw)
		}
	}

	dec.Token() // consommer }

	if !kwargsInjected {
		if !first {
			buf.WriteByte(',')
		}
		buf.Write([]byte(`"chat_template_kwargs":{"enable_thinking":false}`))
	}

	buf.WriteByte('}')
	return buf.Bytes(), nil
}

func makeProxy(targetURL *url.URL, inject bool) http.Handler {
	proxy := &httputil.ReverseProxy{
		Director: func(req *http.Request) {
			req.URL.Scheme = targetURL.Scheme
			req.URL.Host = targetURL.Host
			req.Host = targetURL.Host
			req.Header.Del("Accept-Encoding")
		},
		FlushInterval: -1,
	}

	if !inject {
		return proxy
	}

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost && r.URL.Path == "/v1/chat/completions" {
			modified, err := injectNoThinking(r.Body)
			r.Body.Close()

			if err != nil || modified == nil {
				log.Printf("[inject] error or non-JSON body: %v — forwarding original", err)
				// impossible de rejouer le body original (déjà lu) — renvoyer une erreur
				http.Error(w, "proxy: failed to parse request body", http.StatusBadRequest)
				return
			}

			r.Body = io.NopCloser(bytes.NewReader(modified))
			r.ContentLength = int64(len(modified))
			log.Printf("[inject] POST /v1/chat/completions — enable_thinking=false (%d bytes)", len(modified))
		}
		proxy.ServeHTTP(w, r)
	})
}

func main() {
	flag.Parse()

	targetURL, err := url.Parse(target)
	if err != nil {
		log.Fatalf("invalid target URL: %v", err)
	}

	log.Printf("target       : %s", target)
	log.Printf("port-direct  :%s  →  Open WebUI (pas d'injection)", portDirect)
	log.Printf("port-inject  :%s  →  OpenHands (enable_thinking=false)", portInject)

	go func() {
		log.Fatal(http.ListenAndServe(":"+portDirect, makeProxy(targetURL, false)))
	}()

	log.Fatal(http.ListenAndServe(":"+portInject, makeProxy(targetURL, true)))
}
