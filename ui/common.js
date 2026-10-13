/* Helpers shared by ui/index.html and ui/widget.html (loaded first by both pages). */

/* HTML-escape for text AND attribute values (quotes included: a name containing " used
   to break out of the widget's attributes). */
function esc(s){
  return (s==null?'':String(s)).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
