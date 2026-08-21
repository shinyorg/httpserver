using System.Globalization;
using System.Net;
using System.Text;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>
/// The HTML a browser gets when it <c>GET</c>s a collection: a file manager driven entirely by the
/// mount's own verbs.
/// <para>
/// WebDAV says nothing about <c>GET</c> on a collection, and the operating system's own client is
/// the intended one — but the first thing anyone does with a new mount is open it in a browser, and
/// on a phone that browser is the only client there is. So the page uploads with <c>PUT</c>, makes
/// collections with <c>MKCOL</c>, renames with <c>MOVE</c> and removes with <c>DELETE</c>: no API
/// beside the protocol, and nothing the mount does not already allow.
/// </para>
/// <para>
/// The listing itself is rendered here rather than fetched, so a browser with no script still gets
/// every link. Script adds the verbs that a link cannot express.
/// </para>
/// </summary>
static class WebDavDirectoryPage
{
    /// <summary>One row: a member of the collection being shown.</summary>
    internal readonly record struct Entry(
        string Name,
        string Href,
        bool IsCollection,
        long Size,
        DateTime LastModifiedUtc
    );

    /// <summary>A step in the trail back to the mount root.</summary>
    internal readonly record struct Crumb(string Name, string Href);

    /// <summary>
    /// What the page is allowed to offer. Every control is gated here rather than in script, so a
    /// read-only mount renders a listing with no button that would only earn a 403.
    /// </summary>
    internal readonly record struct Capabilities(
        bool CanWrite,
        bool CanDelete,
        bool CanMove,
        long MaxUploadBytes
    );

    internal sealed record Model(
        string Title,
        string CollectionHref,
        string? ParentHref,
        IReadOnlyList<Crumb> Trail,
        IReadOnlyList<Entry> Entries,
        Capabilities Capabilities
    );


    public static string Render(Model model)
    {
        var caps = model.Capabilities;
        var builder = new StringBuilder(4096);

        builder.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>");
        Text(builder, model.Title);
        builder.Append("</title><style>").Append(Css).Append("</style></head><body");

        // The verbs need somewhere to point, and the collection's own href is the only thing every
        // one of them is built from - an upload is this plus a file name, and nothing else.
        Attribute(builder, "data-href", model.CollectionHref);
        Attribute(builder, "data-max-upload", caps.MaxUploadBytes.ToString(CultureInfo.InvariantCulture));

        if (caps.CanWrite)
            builder.Append(" data-can-write=\"1\"");

        if (caps.CanDelete)
            builder.Append(" data-can-delete=\"1\"");

        if (caps.CanMove)
            builder.Append(" data-can-move=\"1\"");

        builder.Append('>');

        WriteHeader(builder, model);
        WriteTable(builder, model);

        builder.Append("<div class=\"drop\" id=\"drop\" hidden><div>Drop to upload</div></div>");
        builder.Append("<div class=\"toasts\" id=\"toasts\"></div>");
        builder.Append("<dialog id=\"ask\"><form method=\"dialog\"><h2 id=\"ask-title\"></h2>");
        builder.Append("<p id=\"ask-text\"></p><input id=\"ask-input\" autocomplete=\"off\" spellcheck=\"false\">");
        // Cancel is not a submit button, so Enter in the name field lands on the one that is - which
        // is the button anyone typing a name is aiming for.
        builder.Append("<menu><button type=\"button\" id=\"ask-cancel\" class=\"btn\">Cancel</button>");
        builder.Append("<button value=\"ok\" id=\"ask-ok\" class=\"btn primary\"></button></menu></form></dialog>");

        builder.Append("<script>").Append(Script).Append("</script></body></html>");

        return builder.ToString();
    }


    static void WriteHeader(StringBuilder builder, Model model)
    {
        var caps = model.Capabilities;

        builder.Append("<header><nav class=\"trail\">");

        for (var i = 0; i < model.Trail.Count; i++)
        {
            var crumb = model.Trail[i];
            var last = i == model.Trail.Count - 1;

            if (i > 0)
                builder.Append("<span class=\"sep\">/</span>");

            if (last)
            {
                builder.Append("<span class=\"here\">");
                Text(builder, crumb.Name);
                builder.Append("</span>");
            }
            else
            {
                builder.Append("<a href=\"");
                Text(builder, crumb.Href);
                builder.Append("\">");
                Text(builder, crumb.Name);
                builder.Append("</a>");
            }
        }

        builder.Append("</nav>");

        if (caps.CanWrite)
        {
            builder.Append("<div class=\"actions\">");
            builder.Append("<label class=\"btn primary\">Upload<input type=\"file\" id=\"pick\" multiple hidden></label>");
            builder.Append("<button class=\"btn\" id=\"new-folder\">New folder</button>");
            builder.Append("</div>");
        }

        builder.Append("</header>");
    }


    static void WriteTable(StringBuilder builder, Model model)
    {
        var caps = model.Capabilities;

        builder.Append("<main><table><thead><tr><th class=\"name\">Name</th><th class=\"size\">Size</th>");
        builder.Append("<th class=\"when\">Modified</th><th class=\"do\"></th></tr></thead><tbody>");

        // The link out of a collection is a row like any other, because that is where a hand or a
        // thumb already is - and it is absolute for the same reason every other href here is: a
        // browser that arrived without the trailing slash resolves a relative one against the parent.
        if (model.ParentHref is { Length: > 0 } parent)
        {
            builder.Append("<tr class=\"up\"><td class=\"name\"><a href=\"");
            Text(builder, parent);
            builder.Append("\">").Append(UpIcon);
            builder.Append("<span>..</span></a></td><td class=\"size\"></td><td class=\"when\"></td><td class=\"do\"></td></tr>");
        }

        foreach (var entry in model.Entries)
            WriteRow(builder, entry, caps);

        builder.Append("</tbody></table>");

        if (model.Entries.Count == 0)
            builder.Append("<p class=\"empty\">This folder is empty.</p>");

        builder.Append("</main>");
    }


    static void WriteRow(StringBuilder builder, Entry entry, Capabilities caps)
    {
        builder.Append("<tr");
        Attribute(builder, "data-href", entry.Href);
        Attribute(builder, "data-name", entry.Name);

        if (entry.IsCollection)
            builder.Append(" data-dir=\"1\"");

        builder.Append("><td class=\"name\"><a href=\"");
        Text(builder, entry.Href);

        // Opened rather than saved, the same as a link into the mount from anywhere else: an image
        // or a PDF is worth looking at, and saving it is the button next to it.
        builder.Append("\"").Append('>').Append(entry.IsCollection ? FolderIcon : FileIcon).Append("<span>");
        Text(builder, entry.Name);
        builder.Append("</span></a></td><td class=\"size\">");

        if (!entry.IsCollection)
            builder.Append(Size(entry.Size));

        builder.Append("</td><td class=\"when\"><time datetime=\"");
        builder.Append(entry.LastModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        builder.Append("\">");
        builder.Append(entry.LastModifiedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        builder.Append("</time></td>");

        builder.Append("<td class=\"do\">");

        // A folder cannot be saved in one click, and a link that pretends otherwise saves the
        // listing HTML under the folder's name.
        if (!entry.IsCollection)
        {
            builder.Append("<a class=\"icon\" title=\"Download\" aria-label=\"Download\" download=\"");
            Text(builder, entry.Name);
            builder.Append("\" href=\"");
            Text(builder, entry.Href);
            builder.Append("\">").Append(DownloadIcon).Append("</a>");
        }

        if (caps.CanMove)
            builder.Append("<button class=\"icon\" data-act=\"rename\" title=\"Rename\" aria-label=\"Rename\">").Append(RenameIcon).Append("</button>");

        if (caps.CanDelete)
            builder.Append("<button class=\"icon danger\" data-act=\"delete\" title=\"Delete\" aria-label=\"Delete\">").Append(DeleteIcon).Append("</button>");

        builder.Append("</td></tr>");
    }


    static void Attribute(StringBuilder builder, string name, string value)
    {
        builder.Append(' ').Append(name).Append("=\"");
        Text(builder, value);
        builder.Append('"');
    }


    /// <summary>
    /// Everything from the file system goes through here. A file name is attacker-supplied on any
    /// mount that allows writing, and it lands in both text and attribute positions.
    /// </summary>
    static void Text(StringBuilder builder, string value)
        => builder.Append(WebUtility.HtmlEncode(value));


    static string Size(long bytes)
        => bytes switch
        {
            >= 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
            >= 1024L * 1024 => (bytes / 1024d / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
            >= 1024 => (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
        };


    // Filled for a collection and outlined for a file, so the two are told apart by shape and by
    // weight rather than by reading the name.
    const string FolderIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\" class=\"i dir\"><path d=\"M1.5 4.25A1.25 1.25 0 0 1 2.75 3h2.9l1.35 1.6H13.25A1.25 1.25 0 0 1 14.5 5.85v5.4A1.25 1.25 0 0 1 13.25 12.5H2.75A1.25 1.25 0 0 1 1.5 11.25z\"/></svg>";

    const string FileIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\" class=\"i\"><path d=\"M9.2 2H4.6a.6.6 0 0 0-.6.6v10.8a.6.6 0 0 0 .6.6h6.8a.6.6 0 0 0 .6-.6V4.8z\"/><path d=\"M9.2 2v2.8H12\"/></svg>";

    const string UpIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\" class=\"i\"><path d=\"M8 12.5v-8.6M4.4 7.5 8 3.9l3.6 3.6\"/></svg>";

    const string DownloadIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"M8 2.2v8.1M4.6 6.9 8 10.3l3.4-3.4M2.8 13.4h10.4\"/></svg>";

    const string RenameIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"M11.3 2.2 13.8 4.7 6 12.5l-3.3.8.8-3.3z\"/></svg>";

    const string DeleteIcon =
        "<svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"M2.8 4.3h10.4M6.4 4.3V2.4h3.2v1.9M4.4 4.3l.7 9.3h5.8l.7-9.3M6.6 6.7v4.6M9.4 6.7v4.6\"/></svg>";


    const string Css =
        """
        *,*::before,*::after{box-sizing:border-box}
        :root{
          color-scheme:light dark;
          --bg:#fbfbfd;--panel:#fff;--line:#e4e4ea;--ink:#1c1c22;--dim:#6c6c78;
          --accent:#3b62d9;--accent-ink:#fff;--danger:#c8322b;--shade:rgba(20,20,30,.06);
        }
        @media (prefers-color-scheme:dark){
          :root{--bg:#131317;--panel:#1b1b21;--line:#2c2c35;--ink:#ececf1;--dim:#9a9aa8;
                --accent:#7d9bff;--accent-ink:#12121a;--danger:#ff7a72;--shade:rgba(255,255,255,.06)}
        }
        body{margin:0;background:var(--bg);color:var(--ink);
             font:14px/1.5 ui-sans-serif,-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
        header{display:flex;gap:12px;align-items:center;flex-wrap:wrap;
               padding:14px 18px;border-bottom:1px solid var(--line);background:var(--panel);
               position:sticky;top:0;z-index:2}
        .trail{flex:1;min-width:0;font-size:15px;overflow-wrap:anywhere}
        .trail a{color:var(--dim);text-decoration:none}
        .trail a:hover{color:var(--accent);text-decoration:underline}
        .trail .sep{color:var(--dim);margin:0 6px;opacity:.6}
        .trail .here{font-weight:600}
        .actions{display:flex;gap:8px}
        .btn{display:inline-flex;align-items:center;gap:6px;cursor:pointer;font:inherit;
             padding:7px 13px;border-radius:8px;border:1px solid var(--line);
             background:var(--panel);color:var(--ink)}
        .btn:hover{border-color:var(--accent);color:var(--accent)}
        .btn.primary{background:var(--accent);border-color:var(--accent);color:var(--accent-ink)}
        .btn.primary:hover{filter:brightness(1.08);color:var(--accent-ink)}
        .btn.danger{background:var(--danger);border-color:var(--danger);color:#fff}
        .btn.danger:hover{filter:brightness(1.08);color:#fff}
        main{padding:18px}
        table{width:100%;border-collapse:collapse;background:var(--panel);
              border:1px solid var(--line);border-radius:10px;overflow:hidden}
        th,td{text-align:left;padding:9px 12px;border-bottom:1px solid var(--line)}
        th{font-size:12px;letter-spacing:.04em;text-transform:uppercase;color:var(--dim);font-weight:600}
        tbody tr:last-child td{border-bottom:0}
        tbody tr:hover{background:var(--shade)}
        td.name a{display:flex;align-items:center;gap:9px;color:inherit;text-decoration:none;overflow-wrap:anywhere}
        td.name a:hover span{text-decoration:underline}
        .i{width:16px;height:16px;flex:none;fill:none;stroke:var(--dim);
            stroke-width:1.3;stroke-linejoin:round;stroke-linecap:round}
        .i.dir{fill:var(--accent);stroke:none}
        td.size,th.size{width:8.5rem;color:var(--dim);white-space:nowrap}
        td.when,th.when{width:11rem;color:var(--dim);white-space:nowrap}
        td.do,th.do{width:7.5rem;text-align:right;white-space:nowrap}
        .icon{display:inline-flex;border:0;background:none;cursor:pointer;padding:5px;border-radius:6px;
              color:var(--dim);opacity:0;transition:opacity .1s}
        .icon svg{width:15px;height:15px;display:block;fill:none;stroke:currentColor;
                  stroke-width:1.3;stroke-linejoin:round;stroke-linecap:round}
        .icon:hover{color:var(--accent);background:var(--shade)}
        .icon.danger:hover{color:var(--danger)}
        tr:hover .icon,.icon:focus-visible{opacity:1}
        @media (hover:none){.icon{opacity:1}}
        .empty{color:var(--dim);padding:22px 2px;text-align:center}
        .drop{position:fixed;inset:0;z-index:5;display:flex;align-items:center;justify-content:center;
              background:color-mix(in srgb,var(--bg) 78%,transparent);backdrop-filter:blur(2px)}
        .drop div{border:2px dashed var(--accent);color:var(--accent);border-radius:14px;
                  padding:26px 44px;font-size:17px;font-weight:600;background:var(--panel)}
        .toasts{position:fixed;right:16px;bottom:16px;z-index:6;display:flex;
                flex-direction:column;gap:8px;max-width:min(94vw,420px)}
        .toast{background:var(--panel);border:1px solid var(--line);border-left:3px solid var(--accent);
               border-radius:8px;padding:10px 13px;box-shadow:0 6px 22px rgba(0,0,0,.14);overflow-wrap:anywhere}
        .toast.bad{border-left-color:var(--danger);color:var(--danger)}
        .toast .bar{height:3px;border-radius:2px;background:var(--shade);margin-top:8px}
        .toast .bar i{display:block;height:100%;border-radius:2px;background:var(--accent);width:0;transition:width .1s}
        dialog{border:1px solid var(--line);border-radius:12px;background:var(--panel);color:var(--ink);
               padding:18px;min-width:min(92vw,340px)}
        dialog::backdrop{background:rgba(10,10,16,.44)}
        dialog h2{margin:0 0 6px;font-size:16px}
        dialog p{margin:0 0 12px;color:var(--dim);overflow-wrap:anywhere}
        dialog input{width:100%;font:inherit;padding:8px 10px;border-radius:8px;
                     border:1px solid var(--line);background:var(--bg);color:var(--ink)}
        dialog menu{display:flex;gap:8px;justify-content:flex-end;margin:14px 0 0;padding:0}
        [hidden]{display:none!important}
        """;


    /// <summary>
    /// Every button here is one WebDAV request. There is no client state to keep: the page reloads
    /// after a change, because the server's listing is the truth and re-deriving it here would be a
    /// second one.
    /// </summary>
    const string Script =
        """
        (function(){
          var body=document.body;
          var base=body.dataset.href;
          var canWrite=body.dataset.canWrite==='1';
          var canDelete=body.dataset.canDelete==='1';
          var canMove=body.dataset.canMove==='1';
          var maxUpload=parseInt(body.dataset.maxUpload||'0',10);
          var toasts=document.getElementById('toasts');
          var ask=document.getElementById('ask');
          document.getElementById('ask-cancel').addEventListener('click',function(){ask.close('cancel');});

          document.querySelectorAll('time[datetime]').forEach(function(t){
            var d=new Date(t.getAttribute('datetime'));
            if(!isNaN(d)) t.textContent=d.toLocaleString([], {dateStyle:'medium', timeStyle:'short'});
          });

          function toast(text,bad){
            var el=document.createElement('div');
            el.className='toast'+(bad?' bad':'');
            el.textContent=text;
            toasts.appendChild(el);
            if(!bad) setTimeout(function(){el.remove();},4000);
            else el.addEventListener('click',function(){el.remove();});
            return el;
          }

          function url(name,dir){
            return base+encodeURIComponent(name)+(dir?'/':'');
          }

          function why(status){
            if(status===403) return 'not allowed';
            if(status===404) return 'no longer there';
            if(status===405) return 'not allowed';
            if(status===409) return 'the parent folder is gone';
            if(status===412) return 'something already exists there';
            if(status===413) return 'too large';
            if(status===423) return 'locked by another client';
            if(status===507) return 'no room left';
            return 'failed ('+status+')';
          }

          function send(method,target,headers){
            return fetch(target,{method:method,headers:headers||{},credentials:'same-origin',redirect:'follow'})
              .then(function(r){
                if(!r.ok) throw new Error(why(r.status));
                return r;
              });
          }

          // A dialog rather than prompt()/confirm(): those block the page, and a page mid-upload has
          // something to say while they are open.
          function prompt(opts){
            return new Promise(function(resolve){
              document.getElementById('ask-title').textContent=opts.title;
              document.getElementById('ask-text').textContent=opts.text||'';
              document.getElementById('ask-ok').textContent=opts.ok||'OK';
              document.getElementById('ask-ok').className='btn '+(opts.danger?'danger':'primary');
              var input=document.getElementById('ask-input');
              input.hidden=!opts.input;
              input.value=opts.value||'';
              function done(){
                ask.removeEventListener('close',done);
                resolve(ask.returnValue==='ok'?(opts.input?input.value.trim():true):null);
              }
              ask.addEventListener('close',done);
              ask.returnValue='cancel';
              ask.showModal();
              if(opts.input){input.focus();input.select();}
            });
          }

          function upload(file,path){
            return new Promise(function(resolve,reject){
              if(maxUpload>0&&file.size>maxUpload){
                reject(new Error(file.name+' is larger than this server accepts'));
                return;
              }
              var note=toast('Uploading '+(path||file.name)+'...');
              var bar=document.createElement('div');
              bar.className='bar';
              bar.innerHTML='<i></i>';
              note.appendChild(bar);

              var xhr=new XMLHttpRequest();
              xhr.open('PUT',base+(path?path.split('/').map(encodeURIComponent).join('/'):encodeURIComponent(file.name)));
              xhr.withCredentials=true;
              if(file.type) xhr.setRequestHeader('Content-Type',file.type);
              xhr.upload.onprogress=function(e){
                if(e.lengthComputable) bar.firstChild.style.width=(e.loaded/e.total*100)+'%';
              };
              xhr.onload=function(){
                note.remove();
                if(xhr.status>=200&&xhr.status<300) resolve();
                else reject(new Error((path||file.name)+': '+why(xhr.status)));
              };
              xhr.onerror=function(){note.remove();reject(new Error((path||file.name)+': the connection dropped'));};
              xhr.send(file);
            });
          }

          // Sequential: a phone on the other end of a tunnel does not get faster by being asked for
          // ten files at once, and one failure should not leave nine in flight.
          function uploadAll(items){
            var i=0;
            function next(){
              if(i>=items.length){location.reload();return;}
              var item=items[i++];
              var step=item.dir?send('MKCOL',base+item.path.split('/').map(encodeURIComponent).join('/')+'/')
                                  .catch(function(){})            // it may already be there, which is fine
                              :upload(item.file,item.path);
              step.then(next,function(e){toast(e.message,true);next();});
            }
            next();
          }

          function fromFiles(files){
            return Array.prototype.map.call(files,function(f){return {file:f,path:f.name};});
          }

          // A dropped folder arrives as an entry tree rather than a file list, and walking it is the
          // difference between "drop a folder" working and silently doing nothing.
          function fromEntries(items){
            var out=[],pending=0,cap=2000;
            return new Promise(function(resolve){
              function done(){if(pending===0) resolve(out);}
              function walk(entry,prefix){
                if(out.length>=cap) return;
                var path=prefix?prefix+'/'+entry.name:entry.name;
                if(entry.isFile){
                  pending++;
                  entry.file(function(f){out.push({file:f,path:path});pending--;done();},
                             function(){pending--;done();});
                }else if(entry.isDirectory){
                  out.push({dir:true,path:path});
                  pending++;
                  var reader=entry.createReader();
                  (function read(){
                    reader.readEntries(function(list){
                      if(!list.length){pending--;done();return;}
                      list.forEach(function(child){walk(child,path);});
                      read();
                    },function(){pending--;done();});
                  })();
                }
              }
              for(var i=0;i<items.length;i++){
                var entry=items[i].webkitGetAsEntry&&items[i].webkitGetAsEntry();
                if(entry) walk(entry,'');
                else if(items[i].getAsFile&&items[i].getAsFile()) out.push({file:items[i].getAsFile(),path:items[i].getAsFile().name});
              }
              setTimeout(done,0);
            });
          }

          if(canWrite){
            var pick=document.getElementById('pick');
            pick.addEventListener('change',function(){
              if(pick.files.length) uploadAll(fromFiles(pick.files));
            });

            document.getElementById('new-folder').addEventListener('click',function(){
              prompt({title:'New folder',ok:'Create',input:true}).then(function(name){
                if(!name) return;
                send('MKCOL',url(name,true))
                  .then(function(){location.reload();},function(e){toast('New folder: '+e.message,true);});
              });
            });

            var drop=document.getElementById('drop'),depth=0;
            window.addEventListener('dragenter',function(e){
              if(e.dataTransfer&&Array.prototype.indexOf.call(e.dataTransfer.types,'Files')<0) return;
              depth++;drop.hidden=false;
            });
            window.addEventListener('dragover',function(e){e.preventDefault();});
            window.addEventListener('dragleave',function(){if(--depth<=0){depth=0;drop.hidden=true;}});
            window.addEventListener('drop',function(e){
              e.preventDefault();depth=0;drop.hidden=true;
              var dt=e.dataTransfer;
              if(!dt) return;
              if(dt.items&&dt.items.length&&dt.items[0].webkitGetAsEntry)
                fromEntries(dt.items).then(function(items){if(items.length) uploadAll(items);});
              else if(dt.files&&dt.files.length) uploadAll(fromFiles(dt.files));
            });
          }

          document.addEventListener('click',function(e){
            var button=e.target.closest?e.target.closest('button[data-act]'):null;
            if(!button) return;
            var row=button.closest('tr');
            var name=row.dataset.name,href=row.dataset.href,dir=row.dataset.dir==='1';

            if(button.dataset.act==='delete'&&canDelete){
              prompt({
                title:'Delete '+name+'?',
                text:dir?'Everything inside it goes too. This cannot be undone.':'This cannot be undone.',
                ok:'Delete',danger:true
              }).then(function(ok){
                if(!ok) return;
                send('DELETE',href).then(function(){row.remove();},function(err){toast(name+': '+err.message,true);});
              });
            }

            if(button.dataset.act==='rename'&&canMove){
              prompt({title:'Rename',ok:'Rename',input:true,value:name}).then(function(next){
                if(!next||next===name) return;
                send('MOVE',href,{
                  // Absolute, and Overwrite: F - a rename that lands on an existing name should say
                  // so rather than quietly replace whatever was there.
                  'Destination':new URL(url(next,dir),location.href).href,
                  'Overwrite':'F'
                }).then(function(){location.reload();},function(err){toast(name+': '+err.message,true);});
              });
            }
          });
        })();
        """;
}
