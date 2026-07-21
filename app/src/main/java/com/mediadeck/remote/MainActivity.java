package com.mediadeck.remote;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Base64;
import android.util.Log;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public final class MainActivity extends Activity {
    private static final int BG=Color.rgb(9,10,16), CARD=Color.rgb(24,25,36), INK=Color.rgb(246,244,255), MUTED=Color.rgb(161,161,179), PURPLE=Color.rgb(167,139,250);
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private final ExecutorService thumbnails=Executors.newFixedThreadPool(3);
    private final Runnable poll=()->refresh(false);
    private ImageView artwork;
    private TextView source,title,artist,status,elapsed,remaining,scenes;
    private Button previous,play,next,shuffle,repeat,altTab;
    private ChapterSeekBar timeline;
    private SwipeReplayView replay;
    private String base="",deviceKey="",lastTrack="";
    private boolean running,requestPending,userSeeking,altHeld,youtubeAvailable,artworkPending,artworkLoaded,replayAvailable,replayEnabled;
    private long durationMs,positionMs,lastArtworkAttemptMs;
    private int replaySeconds=120;
    private final ArrayList<MediaChapter> chapters=new ArrayList<>();

    @Override public void onCreate(Bundle state){
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        base=getPreferences(0).getString("pc","");
        deviceKey=getPreferences(0).getString("deviceKey","");
        build();
    }

    @Override protected void onResume(){super.onResume();running=true;refresh(true);}
    @Override protected void onPause(){running=false;ui.removeCallbacks(poll);if(altHeld)endAltGesture();super.onPause();}
    @Override protected void onDestroy(){io.shutdownNow();thumbnails.shutdownNow();super.onDestroy();}

    private void build(){
        LinearLayout root=new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BG);
        final int side=dp(6),top=dp(10),bottom=dp(14);
        root.setPadding(side,top,side,bottom);
        root.setOnApplyWindowInsetsListener((v,insets)->{
            int insetTop,insetBottom;
            if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars());insetTop=bars.top;insetBottom=bars.bottom;}
            else{insetTop=insets.getSystemWindowInsetTop();insetBottom=insets.getSystemWindowInsetBottom();}
            v.setPadding(side,top+insetTop,side,bottom+insetBottom);
            return insets;
        });

        LinearLayout topBar=new LinearLayout(this);
        topBar.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout brandBlock=new LinearLayout(this);
        brandBlock.setOrientation(LinearLayout.VERTICAL);
        brandBlock.setGravity(Gravity.CENTER_VERTICAL);
        TextView brand=text("MASHR",20,INK,true);
        brand.setLetterSpacing(.16f);
        brandBlock.addView(brand,new LinearLayout.LayoutParams(-1,dp(25)));
        TextView brandSub=text("MEDIA DECK",9,PURPLE,true);
        brandSub.setLetterSpacing(.20f);
        brandBlock.addView(brandSub,new LinearLayout.LayoutParams(-1,dp(17)));
        topBar.addView(brandBlock,new LinearLayout.LayoutParams(0,dp(42),1));
        Button settings=button("PC SETTINGS");
        settings.setTextSize(12);
        settings.setOnClickListener(v->openPcSettings());
        topBar.addView(settings,new LinearLayout.LayoutParams(-2,dp(42)));
        root.addView(topBar);

        status=text("CONNECTING TO PC...",14,MUTED,true);
        status.setPadding(dp(3),dp(2),0,dp(4));
        root.addView(status);

        LinearLayout card=new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(8),dp(9),dp(8),dp(9));
        card.setBackground(round(CARD,22));
        artwork=new ImageView(this);
        artwork.setScaleType(ImageView.ScaleType.CENTER_CROP);
        artwork.setBackground(round(Color.rgb(42,38,58),18));
        artwork.setContentDescription("Current media artwork. Swipe up for YouTube suggestions.");
        artwork.setOnTouchListener(new View.OnTouchListener(){
            float downX,downY;
            @Override public boolean onTouch(View view,MotionEvent event){
                if(event.getActionMasked()==MotionEvent.ACTION_DOWN){downX=event.getX();downY=event.getY();view.getParent().requestDisallowInterceptTouchEvent(true);return true;}
                if(event.getActionMasked()==MotionEvent.ACTION_UP){view.getParent().requestDisallowInterceptTouchEvent(false);float rise=downY-event.getY(),sideways=Math.abs(downX-event.getX());if(rise>dp(55)&&sideways<dp(100)){view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);showYouTubeSuggestions();}return true;}
                if(event.getActionMasked()==MotionEvent.ACTION_CANCEL)view.getParent().requestDisallowInterceptTouchEvent(false);
                return true;
            }
        });
        int artHeight=Math.min(dp(100),(int)(getResources().getDisplayMetrics().heightPixels*.12f));
        card.addView(artwork,new LinearLayout.LayoutParams(-1,artHeight));

        source=text("PC MEDIA",10,PURPLE,true);
        source.setLetterSpacing(.09f);
        source.setSingleLine(true);
        source.setPadding(0,dp(7),0,dp(2));
        card.addView(source);
        title=text("Waiting for PC media",22,INK,true);
        title.setMinLines(2);
        title.setMaxLines(2);
        card.addView(title);
        artist=text("Start YouTube Music or another player on the PC",14,MUTED,false);
        artist.setMaxLines(1);
        artist.setPadding(0,dp(1),0,dp(4));
        card.addView(artist);

        timeline=new ChapterSeekBar(this);
        timeline.setMax(1000);
        timeline.setProgressTintList(android.content.res.ColorStateList.valueOf(PURPLE));
        timeline.setThumbTintList(android.content.res.ColorStateList.valueOf(PURPLE));
        timeline.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar bar,int progress,boolean fromUser){if(fromUser)elapsed.setText(formatTime(durationMs*progress/1000));}
            public void onStartTrackingTouch(SeekBar bar){userSeeking=true;}
            public void onStopTrackingTouch(SeekBar bar){userSeeking=false;seekTo(durationMs*bar.getProgress()/1000);}
        });
        card.addView(timeline,new LinearLayout.LayoutParams(-1,dp(26)));
        LinearLayout times=new LinearLayout(this);
        elapsed=text("0:00",11,MUTED,false);
        remaining=text("-0:00",11,MUTED,false);
        times.addView(elapsed,new LinearLayout.LayoutParams(0,-2,1));
        scenes=text("SCENES",11,PURPLE,true);
        scenes.setGravity(Gravity.CENTER);
        scenes.setPadding(dp(8),0,dp(8),0);
        scenes.setVisibility(View.INVISIBLE);
        scenes.setOnClickListener(v->showScenes());
        scenes.setContentDescription("Open the chapter scene list");
        times.addView(scenes,new LinearLayout.LayoutParams(-2,-2));
        remaining.setGravity(Gravity.END);
        times.addView(remaining,new LinearLayout.LayoutParams(0,-2,1));
        times.setPadding(dp(5),0,dp(5),dp(4));
        card.addView(times);

        LinearLayout transport=new LinearLayout(this);
        transport.setGravity(Gravity.CENTER);
        previous=largeAction("<  PREV","previous",14);
        previous.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowleft");else control("previous");});
        addWeighted(transport,previous);
        play=largeAction("PLAY","play",16);
        play.setTextColor(Color.BLACK);
        play.setBackground(round(PURPLE,20));
        addWeighted(transport,play);
        next=largeAction("NEXT  >","next",14);
        next.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowright");else control("next");});
        addWeighted(transport,next);
        card.addView(transport,new LinearLayout.LayoutParams(-1,dp(64)));

        LinearLayout seekRow=new LinearLayout(this);
        seekRow.setGravity(Gravity.CENTER);
        seekRow.setPadding(0,dp(5),0,0);
        addWeighted(seekRow,largeAction("-10 SEC","back10",12));
        addWeighted(seekRow,largeAction("+10 SEC","forward10",12));
        card.addView(seekRow,new LinearLayout.LayoutParams(-1,dp(44)));

        LinearLayout volumeRow=new LinearLayout(this);
        volumeRow.setGravity(Gravity.CENTER);
        volumeRow.setPadding(0,dp(5),0,0);
        addWeighted(volumeRow,largeAction("MUTE","mute",12),.75f);
        addWeighted(volumeRow,largeAction("VOL  -","volumedown",14),1.125f);
        addWeighted(volumeRow,largeAction("VOL  +","volumeup",14),1.125f);
        card.addView(volumeRow,new LinearLayout.LayoutParams(-1,dp(60)));

        LinearLayout modeRow=new LinearLayout(this);
        modeRow.setGravity(Gravity.CENTER);
        modeRow.setPadding(0,dp(7),0,0);
        shuffle=largeAction("SHUFFLE","shuffle",12);
        addWeighted(modeRow,shuffle);
        repeat=largeAction("REPEAT","repeat",12);
        addWeighted(modeRow,repeat);
        Button stop=largeAction("STOP","stop",13);
        stop.setTextColor(Color.rgb(254,202,202));
        addWeighted(modeRow,stop);
        card.addView(modeRow,new LinearLayout.LayoutParams(-1,dp(54)));

        LinearLayout utilityRow=new LinearLayout(this);
        utilityRow.setGravity(Gravity.CENTER);
        utilityRow.setPadding(0,dp(5),0,0);
        Button screenshot=largeAction("SCREEN\nSHOT","screenshot",12);
        screenshot.setSingleLine(false);
        screenshot.setLines(2);
        screenshot.setGravity(Gravity.CENTER);
        screenshot.setTextColor(Color.BLACK);
        screenshot.setBackground(round(Color.rgb(196,181,253),20));
        screenshot.setContentDescription("Save a screenshot of the PC using NVIDIA Overlay or Windows");
        screenshot.setOnClickListener(v->takeScreenshot());
        addWeighted(utilityRow,screenshot,.2506f);
        utilityRow.addView(new View(this),new LinearLayout.LayoutParams(dp(4),1));
        Button moveScreen=largeAction("MOVE MEDIA  >","movescreen",12);
        moveScreen.setTextColor(Color.BLACK);
        moveScreen.setBackground(round(Color.rgb(125,211,252),20));
        moveScreen.setContentDescription("Move the selected PC media window to the next monitor");
        moveScreen.setOnClickListener(v->moveScreen());
        addWeighted(utilityRow,moveScreen,.7494f);
        card.addView(utilityRow,new LinearLayout.LayoutParams(-1,dp(57)));

        altTab=largeAction("HOLD ALT + TAB  /  TAP PREV OR NEXT","alttab",13);
        altTab.setTextColor(Color.BLACK);
        altTab.setBackground(round(Color.rgb(251,191,36),20));
        altTab.setOnTouchListener((v,event)->{
            if(event.getActionMasked()==MotionEvent.ACTION_DOWN){v.getParent().requestDisallowInterceptTouchEvent(true);beginAltGesture();return true;}
            if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL){v.getParent().requestDisallowInterceptTouchEvent(false);endAltGesture();return true;}
            return true;
        });
        LinearLayout.LayoutParams altLp=new LinearLayout.LayoutParams(-1,dp(58));
        altLp.setMargins(dp(2),dp(5),dp(2),0);
        card.addView(altTab,altLp);

        replay=new SwipeReplayView(this::handleReplayGesture);
        LinearLayout.LayoutParams replayLp=new LinearLayout.LayoutParams(-1,dp(62));
        replayLp.setMargins(dp(2),dp(5),dp(2),0);
        card.addView(replay,replayLp);
        root.addView(card);
        setContentView(root);
        root.requestApplyInsets();
    }

    private void refresh(boolean showConnecting){
        if(requestPending)return;
        ui.removeCallbacks(poll);
        if(deviceKey.isEmpty()){showPairingNeeded();return;}
        requestPending=true;
        if(showConnecting){status.setText(base.isEmpty()?"FINDING PC...":"CONNECTING TO PC...");status.setTextColor(MUTED);}
        io.execute(()->{
            try{
                JSONObject data=new JSONObject(get("/api/now"));
                ui.post(()->apply(data));
            }catch(Exception first){
                if(isUnauthorized(first)){clearPairing();ui.post(this::showPairingNeeded);return;}
                try{
                    String discovered=discoverPc();
                    if(discovered!=null){
                        base=discovered;
                        getPreferences(0).edit().putString("pc",base).apply();
                        JSONObject data=new JSONObject(get("/api/now"));
                        ui.post(()->apply(data));
                        return;
                    }
                }catch(Exception second){if(isUnauthorized(second)){clearPairing();ui.post(this::showPairingNeeded);return;}}
                ui.post(this::showError);
            }
        });
    }

    private void apply(JSONObject data){
        requestPending=false;
        String newTitle=data.optString("title","Nothing playing"),newArtist=data.optString("artist","Start media on your PC");
        boolean playing=data.optBoolean("playing");
        youtubeAvailable=data.optBoolean("youtubeAvailable",false);
        String sourceId=data.optString("source","PC MEDIA");
        String sourceLabel=friendlySource(sourceId);
        source.setText(sourceLabel+(youtubeAvailable?"  /  SWIPE UP FOR PICKS":""));
        title.setText(newTitle);
        artist.setText(newArtist);
        play.setText(playing?"PAUSE":"PLAY");
        play.setOnClickListener(v->control(playing?"pause":"play"));
        durationMs=data.optLong("durationMs");
        positionMs=Math.min(data.optLong("positionMs"),durationMs);
        updateChapters(data.optJSONArray("chapters"));
        if(!userSeeking)timeline.setProgress(durationMs>0?(int)(positionMs*1000/durationMs):0);
        timeline.setChapters(chapters,durationMs);
        timeline.setEnabled(durationMs>0);
        elapsed.setText(formatTime(positionMs));
        remaining.setText("-"+formatTime(Math.max(0,durationMs-positionMs)));
        replayAvailable=data.optBoolean("instantReplayAvailable",false);
        replayEnabled=data.optBoolean("instantReplayEnabled",false);
        replaySeconds=Math.max(15,data.optInt("instantReplaySeconds",120));
        replay.setReplayState(replayAvailable,replayEnabled,replaySeconds);
        shuffle.setText(data.optBoolean("shuffle")?"SHUFFLE ON":"SHUFFLE");
        String repeatMode=data.optString("repeat","none");
        repeat.setText(repeatMode.equals("track")?"REPEAT 1":repeatMode.equals("list")?"REPEAT ALL":"REPEAT");
        status.setText("PC CONNECTED  /  SIGNED");
        status.setTextColor(PURPLE);
        String key=sourceId+'\n'+newTitle+'\n'+newArtist;
        long now=SystemClock.elapsedRealtime();
        boolean trackChanged=!key.equals(lastTrack);
        if(trackChanged){lastTrack=key;artworkLoaded=false;lastArtworkAttemptMs=0;artwork.setImageDrawable(null);}
        long retryDelay=artworkLoaded?300_000L:10_000L;
        if(!artworkPending&&(trackChanged||now-lastArtworkAttemptMs>=retryDelay)){lastArtworkAttemptMs=now;loadArtwork();}
        schedule();
    }

    private void showPairingNeeded(){requestPending=false;status.setText("PAIRING REQUIRED  /  TAP PC SETTINGS");status.setTextColor(Color.rgb(251,191,36));schedule();}
    private void showError(){requestPending=false;status.setText("PC OFFLINE  /  TAP PC SETTINGS");status.setTextColor(Color.rgb(248,113,113));schedule();}
    private void schedule(){if(running){ui.removeCallbacks(poll);ui.postDelayed(poll,2500);}}

    private void loadArtwork(){
        artworkPending=true;
        io.execute(()->{
            HttpURLConnection connection=null;
            try{
                String path="/api/art?track="+System.currentTimeMillis();
                connection=openConnection(path,"GET",true);
                int code=connection.getResponseCode();
                if(code==401)throw new IOException("HTTP 401");
                if(code>=200&&code<300){
                    Bitmap bitmap=BitmapFactory.decodeStream(connection.getInputStream());
                    if(bitmap!=null){Log.i("MASHRMediaDeck","Artwork loaded "+bitmap.getWidth()+"x"+bitmap.getHeight());ui.post(()->{artworkPending=false;artworkLoaded=true;if(!isFinishing())artwork.setImageBitmap(bitmap);});return;}
                }
                Log.w("MASHRMediaDeck","Artwork request returned HTTP "+code);
            }catch(Exception error){Log.w("MASHRMediaDeck","Artwork load failed",error);}
            finally{if(connection!=null)connection.disconnect();}
            ui.post(()->{artworkPending=false;artworkLoaded=false;});
        });
    }

    private void showYouTubeSuggestions(){
        if(!youtubeAvailable){Toast.makeText(this,"Open a YouTube video with the MASHR Media Deck browser helper enabled",Toast.LENGTH_LONG).show();return;}
        status.setText("LOADING YOUTUBE PICKS...");
        io.execute(()->{
            try{
                JSONArray items=new JSONArray(get("/api/youtube/suggestions"));
                ui.post(()->openSuggestionDialog(items));
            }catch(Exception error){ui.post(()->Toast.makeText(this,"Could not load YouTube suggestions",Toast.LENGTH_SHORT).show());}
        });
    }

    private void openSuggestionDialog(JSONArray items){
        if(items.length()==0){Toast.makeText(this,"No YouTube suggestions are ready yet",Toast.LENGTH_SHORT).show();return;}
        Dialog dialog=new Dialog(this);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout panel=new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(12),dp(12),dp(12),dp(14));
        panel.setBackground(round(Color.rgb(17,18,27),22));
        TextView heading=text("YOUTUBE PICKS  /  TAP TO PLAY",16,INK,true);
        heading.setGravity(Gravity.CENTER);
        heading.setPadding(0,dp(2),0,dp(10));
        panel.addView(heading);
        GridLayout grid=new GridLayout(this);
        grid.setColumnCount(3);
        grid.setRowCount(3);
        for(int index=0;index<Math.min(9,items.length());index++){
            JSONObject item=items.optJSONObject(index);
            if(item==null)continue;
            String videoId=item.optString("videoId","");
            String videoTitle=item.optString("title","YouTube video");
            if(!videoId.matches("[A-Za-z0-9_-]{11}"))continue;
            LinearLayout tile=new LinearLayout(this);
            tile.setOrientation(LinearLayout.VERTICAL);
            tile.setPadding(dp(3),dp(3),dp(3),dp(4));
            tile.setBackground(round(Color.rgb(34,35,49),12));
            ImageView thumbnail=new ImageView(this);
            thumbnail.setScaleType(ImageView.ScaleType.CENTER_CROP);
            thumbnail.setBackgroundColor(Color.rgb(51,52,69));
            tile.addView(thumbnail,new LinearLayout.LayoutParams(-1,dp(62)));
            TextView label=text(videoTitle,11,INK,true);
            label.setMaxLines(2);
            label.setGravity(Gravity.CENTER);
            label.setPadding(dp(2),dp(4),dp(2),0);
            tile.addView(label,new LinearLayout.LayoutParams(-1,dp(38)));
            tile.setOnClickListener(v->{dialog.dismiss();playYouTube(videoId,videoTitle);});
            GridLayout.LayoutParams params=new GridLayout.LayoutParams();
            params.width=0;
            params.height=dp(108);
            params.columnSpec=GridLayout.spec(GridLayout.UNDEFINED,1f);
            params.setMargins(dp(3),dp(3),dp(3),dp(3));
            grid.addView(tile,params);
            loadThumbnail("https://i.ytimg.com/vi/"+videoId+"/mqdefault.jpg",thumbnail);
        }
        panel.addView(grid,new LinearLayout.LayoutParams(-1,-2));
        Button close=button("CLOSE");
        close.setOnClickListener(v->dialog.dismiss());
        LinearLayout.LayoutParams closeParams=new LinearLayout.LayoutParams(-1,dp(44));
        closeParams.setMargins(dp(3),dp(9),dp(3),0);
        panel.addView(close,closeParams);
        dialog.setContentView(panel);
        Window window=dialog.getWindow();
        if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.7f);window.setGravity(Gravity.BOTTOM);window.setLayout(WindowManager.LayoutParams.MATCH_PARENT,WindowManager.LayoutParams.WRAP_CONTENT);}
        dialog.setOnShowListener(ignored->{Window shown=dialog.getWindow();if(shown!=null)shown.setLayout(WindowManager.LayoutParams.MATCH_PARENT,WindowManager.LayoutParams.WRAP_CONTENT);});
        dialog.show();
    }

    private void loadThumbnail(String address,ImageView target){
        thumbnails.execute(()->{
            HttpURLConnection connection=null;
            try{
                connection=(HttpURLConnection)new URL(address).openConnection();
                connection.setConnectTimeout(2500);
                connection.setReadTimeout(2500);
                Bitmap bitmap=BitmapFactory.decodeStream(connection.getInputStream());
                if(bitmap!=null)ui.post(()->{if(!isFinishing())target.setImageBitmap(bitmap);});
            }catch(Exception ignored){}finally{if(connection!=null)connection.disconnect();}
        });
    }

    private void playYouTube(String videoId,String videoTitle){
        io.execute(()->{
            try{post("/api/youtube/play?videoId="+videoId);ui.post(()->Toast.makeText(this,"Playing: "+videoTitle,Toast.LENGTH_SHORT).show());}
            catch(Exception error){ui.post(()->Toast.makeText(this,"That suggestion expired - swipe up again",Toast.LENGTH_SHORT).show());}
        });
    }

    private void updateChapters(JSONArray values){
        chapters.clear();
        if(values!=null){
            for(int index=0;index<Math.min(40,values.length());index++){
                JSONObject item=values.optJSONObject(index);
                if(item==null)continue;
                long marker=item.optLong("positionMs",-1);
                String label=item.optString("title","").trim();
                if(marker<0||label.isEmpty())continue;
                chapters.add(new MediaChapter(marker,label.substring(0,Math.min(120,label.length()))));
            }
        }
        scenes.setText(chapters.size()+" SCENES");
        scenes.setVisibility(chapters.size()>=2?View.VISIBLE:View.INVISIBLE);
    }

    private void showScenes(){
        if(chapters.size()<2){Toast.makeText(this,"No chapter list is published for this video",Toast.LENGTH_SHORT).show();return;}
        int active=0;
        for(int index=0;index<chapters.size();index++){if(chapters.get(index).positionMs<=positionMs)active=index;else break;}
        String[] labels=new String[chapters.size()];
        for(int index=0;index<chapters.size();index++){
            MediaChapter chapter=chapters.get(index);
            labels[index]=(index==active?"▶  ":"     ")+formatTime(chapter.positionMs)+"  "+chapter.title;
        }
        new AlertDialog.Builder(this)
            .setTitle("SCENES  /  TAP TO JUMP")
            .setItems(labels,(dialog,index)->{MediaChapter chapter=chapters.get(index);seekTo(chapter.positionMs);Toast.makeText(this,"Jumping to "+chapter.title,Toast.LENGTH_SHORT).show();})
            .setNegativeButton("CLOSE",null)
            .show();
    }

    private void handleReplayGesture(){
        if(!replayAvailable){Toast.makeText(this,"NVIDIA Instant Replay is unavailable. Check NVIDIA Overlay settings on the PC.",Toast.LENGTH_LONG).show();return;}
        final boolean save=replayEnabled;
        io.execute(()->{
            try{
                JSONObject result=new JSONObject(post(save?"/api/control/instantreplay":"/api/control/replayarm"));
                int seconds=Math.max(15,result.optInt("bufferSeconds",replaySeconds));
                ui.post(()->Toast.makeText(this,save?"Saving the last "+formatTime(seconds*1000L)+" of gameplay":"Replay buffer is arming - save once the track turns green",Toast.LENGTH_LONG).show());
            }catch(Exception error){ui.post(()->Toast.makeText(this,"Replay failed: "+safeMessage(error),Toast.LENGTH_LONG).show());}
            ui.postDelayed(()->refresh(false),1600);
        });
    }

    private void control(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}
    private void takeScreenshot(){io.execute(()->{try{JSONObject captured=new JSONObject(post("/api/control/screenshot"));String provider=captured.optString("provider","PC");ui.post(()->Toast.makeText(this,"Screenshot saved by "+provider,Toast.LENGTH_SHORT).show());}catch(Exception error){ui.post(()->Toast.makeText(this,"Screenshot failed: "+safeMessage(error),Toast.LENGTH_LONG).show());}});}
    private void moveScreen(){io.execute(()->{try{JSONObject moved=new JSONObject(post("/api/control/movescreen"));String display=moved.optString("display","next screen");ui.post(()->Toast.makeText(this,"Media moved to "+display,Toast.LENGTH_SHORT).show());}catch(Exception error){ui.post(()->Toast.makeText(this,"Could not move media: "+safeMessage(error),Toast.LENGTH_LONG).show());}});}
    private void sendKeyCommand(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}});}
    private void beginAltGesture(){if(altHeld)return;altHeld=true;altTab.setText("ALT HELD  /  CHOOSE A WINDOW");altTab.setBackground(round(Color.rgb(248,113,113),20));previous.setText("<  WINDOW");next.setText("WINDOW  >");sendKeyCommand("altdown");}
    private void endAltGesture(){if(!altHeld)return;altHeld=false;sendKeyCommand("altup");altTab.setText("HOLD ALT + TAB  /  TAP PREV OR NEXT");altTab.setBackground(round(Color.rgb(251,191,36),20));previous.setText("<  PREV");next.setText("NEXT  >");}
    private void seekTo(long positionMs){io.execute(()->{try{post("/api/seek?positionMs="+positionMs);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}

    private void openPcSettings(){
        LinearLayout form=new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20),dp(4),dp(20),0);
        EditText address=new EditText(this);
        address.setSingleLine();
        address.setHint("PC address (auto-detect if blank)");
        address.setText(base);
        address.setSelectAllOnFocus(true);
        address.setTextSize(17);
        form.addView(address,new LinearLayout.LayoutParams(-1,dp(56)));
        EditText code=new EditText(this);
        code.setSingleLine();
        code.setHint(deviceKey.isEmpty()?"6-digit tray pairing code":"Pairing code (only after reset)");
        code.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        code.setTextSize(17);
        form.addView(code,new LinearLayout.LayoutParams(-1,dp(56)));
        new AlertDialog.Builder(this)
            .setTitle("Connect securely")
            .setMessage("Right-click the MASHR Media Deck shield in the PC tray for its one-time code. This is local device pairing, not a YouTube login.")
            .setView(form)
            .setNegativeButton("CANCEL",null)
            .setPositiveButton(deviceKey.isEmpty()?"PAIR":"CONNECT",(dialog,which)->{
                String value=cleanAddress(address.getText().toString());
                String pairingCode=code.getText().toString().trim();
                if(!value.isEmpty()){base=value;getPreferences(0).edit().putString("pc",base).apply();}
                if(pairingCode.isEmpty()&&!deviceKey.isEmpty()){lastTrack="";refresh(true);return;}
                if(pairingCode.length()!=6){Toast.makeText(this,"Enter the 6-digit code from the PC tray",Toast.LENGTH_SHORT).show();return;}
                pairWithPc(pairingCode);
            }).show();
    }

    private void pairWithPc(String code){
        requestPending=true;
        status.setText("PAIRING WITH PC...");
        status.setTextColor(Color.rgb(251,191,36));
        io.execute(()->{
            try{
                if(base.isEmpty()){
                    String discovered=discoverPc();
                    if(discovered==null)throw new IOException("PC not found");
                    base=discovered;
                    getPreferences(0).edit().putString("pc",base).apply();
                }
                HttpURLConnection connection=openConnection("/api/pair","POST",false);
                connection.setRequestProperty("X-MediaDeck-Pairing-Code",code);
                connection.setDoOutput(true);
                connection.getOutputStream().close();
                String response=readResponse(connection);
                connection.disconnect();
                String received=new JSONObject(response).optString("key","");
                if(received.isEmpty())throw new IOException("No key returned");
                if(Base64.decode(received,Base64.DEFAULT).length!=32)throw new IOException("Invalid pairing key");
                deviceKey=received;
                getPreferences(0).edit().putString("deviceKey",deviceKey).apply();
                requestPending=false;
                ui.post(()->{Toast.makeText(this,"Phone paired securely",Toast.LENGTH_SHORT).show();lastTrack="";refresh(true);});
            }catch(Exception error){requestPending=false;ui.post(()->{status.setText("PAIRING FAILED  /  CHECK THE TRAY CODE");status.setTextColor(Color.rgb(248,113,113));Toast.makeText(this,"Pairing failed: "+safeMessage(error),Toast.LENGTH_LONG).show();});}
        });
    }

    private String get(String path)throws Exception{
        HttpURLConnection connection=openConnection(path,"GET",true);
        try{return readResponse(connection);}finally{connection.disconnect();}
    }

    private String post(String path)throws Exception{
        HttpURLConnection connection=openConnection(path,"POST",true);
        connection.setDoOutput(true);
        try{connection.getOutputStream().close();return readResponse(connection);}finally{connection.disconnect();}
    }

    private HttpURLConnection openConnection(String path,String method,boolean authenticated)throws Exception{
        if(base.isEmpty())throw new IOException("No saved PC address");
        HttpURLConnection connection=(HttpURLConnection)new URL(url(path)).openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(1800);
        connection.setReadTimeout(2500);
        connection.setUseCaches(false);
        if(authenticated)authorize(connection,method,path);
        return connection;
    }

    private void authorize(HttpURLConnection connection,String method,String path)throws Exception{
        if(deviceKey.isEmpty())throw new IOException("Pairing required");
        long timestamp=System.currentTimeMillis()/1000L;
        String nonce=UUID.randomUUID().toString().replace("-","");
        String canonical=method.toUpperCase(Locale.US)+"\n"+path+"\n"+timestamp+"\n"+nonce;
        Mac mac=Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(Base64.decode(deviceKey,Base64.DEFAULT),"HmacSHA256"));
        String signature=Base64.encodeToString(mac.doFinal(canonical.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
        connection.setRequestProperty("X-MediaDeck-Time",Long.toString(timestamp));
        connection.setRequestProperty("X-MediaDeck-Nonce",nonce);
        connection.setRequestProperty("X-MediaDeck-Signature",signature);
    }

    private String readResponse(HttpURLConnection connection)throws Exception{
        int responseCode=connection.getResponseCode();
        InputStream stream=responseCode>=200&&responseCode<300?connection.getInputStream():connection.getErrorStream();
        String body=stream==null?"":readAll(stream);
        if(responseCode<200||responseCode>=300)throw new IOException("HTTP "+responseCode+(body.isEmpty()?"":" - "+body));
        return body;
    }

    private String readAll(InputStream stream)throws IOException{
        try(BufferedReader reader=new BufferedReader(new InputStreamReader(stream,StandardCharsets.UTF_8))){
            StringBuilder result=new StringBuilder();
            String line;
            while((line=reader.readLine())!=null)result.append(line);
            return result.toString();
        }
    }

    private String discoverPc(){
        try(DatagramSocket socket=new DatagramSocket()){
            socket.setBroadcast(true);
            socket.setSoTimeout(1400);
            byte[] query="MEDIADECK_DISCOVER".getBytes(StandardCharsets.UTF_8);
            socket.send(new DatagramPacket(query,query.length,InetAddress.getByName("255.255.255.255"),43822));
            byte[] buffer=new byte[64];
            DatagramPacket reply=new DatagramPacket(buffer,buffer.length);
            socket.receive(reply);
            String message=new String(reply.getData(),0,reply.getLength(),StandardCharsets.UTF_8);
            return message.equals("MEDIADECK:43821")?reply.getAddress().getHostAddress():null;
        }catch(Exception ignored){return null;}
    }

    private void clearPairing(){deviceKey="";getPreferences(0).edit().remove("deviceKey").apply();}
    private boolean isUnauthorized(Exception error){return error.getMessage()!=null&&(error.getMessage().contains("HTTP 401")||error.getMessage().contains("HTTP 409"));}
    private String safeMessage(Exception error){String message=error.getMessage();if(message==null)return "unknown error";return message.length()>120?message.substring(0,120):message;}
    private String cleanAddress(String value){String result=value.trim().replaceFirst("^https?://","");int slash=result.indexOf('/');if(slash>=0)result=result.substring(0,slash);int colon=result.indexOf(':');if(colon>=0)result=result.substring(0,colon);return result;}
    private String url(String path){return "http://"+base+":43821"+path;}
    private Button action(String label,String command){Button button=button(label);button.setOnClickListener(v->control(command));return button;}
    private Button largeAction(String label,String command,int size){Button button=action(label,command);button.setTextSize(size);button.setSingleLine(true);button.setPadding(dp(4),0,dp(4),0);return button;}
    private void addWeighted(LinearLayout row,View view){addWeighted(row,view,1f);}
    private void addWeighted(LinearLayout row,View view,float weight){LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(0,-1,weight);params.setMargins(dp(2),0,dp(2),0);row.addView(view,params);}
    private String formatTime(long millis){long total=Math.max(0,millis/1000),hours=total/3600,minutes=(total%3600)/60,seconds=total%60;return hours>0?String.format(Locale.US,"%d:%02d:%02d",hours,minutes,seconds):String.format(Locale.US,"%d:%02d",minutes,seconds);}
    private String friendlySource(String value){String lower=value.toLowerCase(Locale.US);if(lower.contains("vlc"))return "VLC / PC";if(lower.contains("brave"))return "BRAVE / PC";if(lower.contains("chrome"))return "CHROME / PC";if(lower.contains("spotify"))return "SPOTIFY / PC";return "PC MEDIA";}
    private TextView text(String value,int size,int color,boolean bold){TextView view=new TextView(this);view.setText(value);view.setTextSize(size);view.setTextColor(color);view.setTypeface(Typeface.DEFAULT,bold?Typeface.BOLD:Typeface.NORMAL);view.setLineSpacing(0,1.1f);return view;}
    private Button button(String value){Button button=new Button(this);button.setText(value);button.setTextColor(INK);button.setTextSize(11);button.setTypeface(Typeface.DEFAULT_BOLD);button.setMinHeight(0);button.setMinWidth(0);button.setPadding(dp(16),0,dp(16),0);button.setBackground(round(Color.rgb(47,44,67),20));return button;}
    private GradientDrawable round(int color,int radius){GradientDrawable drawable=new GradientDrawable();drawable.setColor(color);drawable.setCornerRadius(dp(radius));return drawable;}
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}

    private static final class MediaChapter {
        final long positionMs;
        final String title;
        MediaChapter(long positionMs,String title){this.positionMs=positionMs;this.title=title;}
    }

    private final class ChapterSeekBar extends SeekBar {
        private final Paint marker=new Paint(Paint.ANTI_ALIAS_FLAG);
        private long[] positions=new long[0];
        private long chapterDurationMs;
        ChapterSeekBar(Activity context){super(context);marker.setColor(Color.rgb(125,211,252));}
        void setChapters(ArrayList<MediaChapter> values,long duration){
            chapterDurationMs=duration;
            positions=new long[values.size()];
            for(int index=0;index<values.size();index++)positions[index]=values.get(index).positionMs;
            setContentDescription(values.size()>=2?"Playback timeline with "+values.size()+" scene markers. Tap SCENES for the list.":"Playback timeline");
            invalidate();
        }
        @Override protected void onDraw(Canvas canvas){
            super.onDraw(canvas);
            if(chapterDurationMs<=0||positions.length<2)return;
            float left=getPaddingLeft(),width=getWidth()-getPaddingLeft()-getPaddingRight(),center=getHeight()/2f,half=getResources().getDisplayMetrics().density*1.5f,height=dp(5);
            for(long position:positions){
                if(position<=0||position>=chapterDurationMs)continue;
                float x=left+width*Math.min(1f,(float)position/chapterDurationMs);
                canvas.drawRoundRect(x-half,center-height,x+half,center+height,half,half,marker);
            }
        }
    }

    private final class SwipeReplayView extends View {
        private final Paint track=new Paint(Paint.ANTI_ALIAS_FLAG),fill=new Paint(Paint.ANTI_ALIAS_FLAG),handle=new Paint(Paint.ANTI_ALIAS_FLAG),label=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Runnable activate;
        private float progress,startY;
        private boolean tracking,complete;
        private String readyText="CHECKING NVIDIA REPLAY...",completeText="REPLAY REQUEST SENT";
        SwipeReplayView(Runnable activate){super(MainActivity.this);this.activate=activate;setFocusable(true);fill.setColor(Color.rgb(34,197,94));handle.setColor(Color.rgb(236,253,245));label.setColor(Color.WHITE);label.setTextAlign(Paint.Align.CENTER);label.setTypeface(Typeface.DEFAULT_BOLD);label.setTextSize(getResources().getDisplayMetrics().scaledDensity*14);setReplayState(false,false,120);}
        void setReplayState(boolean available,boolean enabled,int seconds){
            track.setColor(!available?Color.rgb(69,36,36):enabled?Color.rgb(20,83,45):Color.rgb(120,53,15));
            readyText=!available?"NVIDIA REPLAY UNAVAILABLE":enabled?"SWIPE TO SAVE "+formatTime(seconds*1000L)+"  >":"SWIPE TO ARM REPLAY  >";
            completeText=enabled?"REPLAY SAVE REQUEST SENT":"REPLAY ARM REQUEST SENT";
            setContentDescription(readyText);
            invalidate();
        }
        @Override protected void onDraw(Canvas canvas){super.onDraw(canvas);float radius=getHeight()/2f;RectF bounds=new RectF(0,0,getWidth(),getHeight());canvas.drawRoundRect(bounds,radius,radius,track);float knob=dp(22),left=knob,right=getWidth()-knob,x=left+(right-left)*progress;if(progress>0){RectF active=new RectF(0,0,x,getHeight());canvas.drawRoundRect(active,radius,radius,fill);}canvas.drawCircle(x,getHeight()/2f,knob,handle);String text=complete?completeText:tracking?"KEEP SWIPING  "+Math.round(progress*100)+"%":readyText;Paint.FontMetrics metrics=label.getFontMetrics();canvas.drawText(text,getWidth()/2f,getHeight()/2f-(metrics.ascent+metrics.descent)/2,label);}
        @Override public boolean onTouchEvent(MotionEvent event){float knob=dp(22),usable=Math.max(1,getWidth()-2*knob);switch(event.getActionMasked()){case MotionEvent.ACTION_DOWN:if(complete||event.getX()>getWidth()*.30f)return true;tracking=true;startY=event.getY();progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));getParent().requestDisallowInterceptTouchEvent(true);invalidate();return true;case MotionEvent.ACTION_MOVE:if(!tracking)return true;if(Math.abs(event.getY()-startY)>dp(42)){cancelSwipe();return true;}progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));invalidate();return true;case MotionEvent.ACTION_UP:if(tracking&&progress>=.85f){tracking=false;complete=true;progress=1;performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);activate.run();invalidate();postDelayed(()->{complete=false;progress=0;invalidate();},2200);}else cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;case MotionEvent.ACTION_CANCEL:cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;default:return true;}}
        private void cancelSwipe(){tracking=false;progress=0;invalidate();}
    }
}
